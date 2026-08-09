using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Data;

using Spectre.Console;

namespace Apache.Cosmos.Sample
{

    /// <summary>
    /// Joins a Cosmos DB container to a CSV file, through a view, in one SQL statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cosmos has no relational join — its <c>JOIN</c> cross-products a document with its own nested
    /// arrays — so a query spanning it and anything else has to be joined outside the service. The
    /// interesting part is what that costs: the adapter takes the join keys from the other side and
    /// sends them with the statement, so only the documents that could match come back.
    /// </para>
    /// <para>
    /// Every statement Cosmos is given is printed, so the difference is visible rather than claimed.
    /// </para>
    /// </remarks>
    static class Program
    {

        static async Task<int> Main()
        {
            try
            {
                return await RunAsync();
            }
            catch (Exception e)
            {
                // Printed as a chain, because the useful sentence is usually several causes down: a
                // model failure surfaces as "error instantiating schema", and what actually went wrong
                // is underneath it, sometimes on the Java side of the bridge.
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Rule("[red]Failed[/]").LeftJustified().RuleStyle("red"));

                for (var cause = e; cause is not null; cause = Next(cause))
                    AnsiConsole.MarkupLine("  [red]{0}[/]: {1}", cause.GetType().Name, Markup.Escape(FirstLine(cause.Message)));

                return 1;
            }
        }

        static string FirstLine(string? message)
        {
            if (string.IsNullOrEmpty(message))
                return "";

            var end = message.IndexOfAny(new[] { '\r', '\n' });
            return end < 0 ? message : message[..end];
        }

        static Exception? Next(Exception e)
        {
            if (e.InnerException is Exception inner)
                return inner;

            // IKVM surfaces a Java exception as a .NET one, but its cause hangs off the Java side.
            if (e is java.lang.Throwable throwable && throwable.getCause() is java.lang.Throwable cause && ReferenceEquals(cause, throwable) == false)
                return cause;

            return null;
        }

        static async Task<int> RunAsync()
        {
            AnsiConsole.Write(new FigletText("Calcite + Cosmos").Color(Color.Teal));
            AnsiConsole.MarkupLine("[dim]Two adapters, one join, through a view.[/]");
            AnsiConsole.WriteLine();

            if (await Sources.WhatIsMissingAsync() is string missing)
            {
                AnsiConsole.Write(new Panel(Markup.Escape(missing))
                    .Header("[yellow]Not ready[/]")
                    .BorderColor(Color.Yellow));

                return 1;
            }

            // A model names its schema factories by type name, resolved through IKVM, and a factory is
            // only found if its assembly is loaded — which for a name appearing nowhere but in a JSON
            // string means touching it here. A discarded typeof() is not enough, since the compiler can
            // elide it, so both are reached through something with a runtime effect.
            _ = new Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory();
            _ = org.apache.calcite.adapter.csv.CsvSchemaFactory.INSTANCE;

            int suppliers = 0, documents = 0;

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Seeding both sources…", async _ =>
                {
                    suppliers = Sources.EnsureCsv();
                    documents = await Sources.EnsureCosmosAsync();
                });

            var sources = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey)
                .AddColumn(new TableColumn("[bold]Source[/]"))
                .AddColumn(new TableColumn("[bold]Adapter[/]"))
                .AddColumn(new TableColumn("[bold]Holds[/]"));

            sources.AddRow("[teal]SUPPLIERS[/]", "Calcite CSV", $"{suppliers} rows");
            sources.AddRow("[teal]COSMOS[/]", "Apache.Calcite.Cosmos.Adapter", $"{documents} documents");
            sources.AddRow("[teal]SALES[/]", "[dim]a view over both[/]", "[dim]PRODUCT_SUPPLIERS[/]");

            AnsiConsole.Write(sources);
            AnsiConsole.WriteLine();

            using var watcher = new CosmosWatcher();

            await using var connection = new CalciteConnection(new CalciteConnectionStringBuilder
            {
                Model = "inline:" + Model,
                CaseSensitive = true,
            }.ConnectionString);

            await connection.OpenAsync();

            await Section(connection, watcher,
                "The CSV side on its own",
                """SELECT * FROM "SUPPLIERS"."SUPPLIERS" ORDER BY "PRODUCT" """);

            await Section(connection, watcher,
                "The Cosmos side on its own",
                """SELECT "id", "category" FROM "COSMOS"."products" ORDER BY "id" """);

            await Section(connection, watcher,
                "The view — joined across both adapters",
                """SELECT * FROM "SALES"."PRODUCT_SUPPLIERS" ORDER BY "PRODUCT" """);

            await Section(connection, watcher,
                "A predicate on the Cosmos side, pushed into the statement",
                """SELECT "PRODUCT", "SUPPLIER", "PRICE" FROM "SALES"."PRODUCT_SUPPLIERS" WHERE "PRICE" > 1000""");

            await Section(connection, watcher,
                "An aggregate over columns both sides contribute to",
                """SELECT "SUPPLIER", COUNT(*) AS "LINES", SUM("PRICE") AS "TOTAL" FROM "SALES"."PRODUCT_SUPPLIERS" GROUP BY "SUPPLIER" ORDER BY "SUPPLIER" """);

            AnsiConsole.Write(new Panel(new Markup(
                    $"Cosmos was asked [bold]{watcher.Statements}[/] time(s) for [bold]{watcher.Charge:0.##}[/] RU in total.\n" +
                    $"The container holds [bold]{documents}[/] documents; the supplier file names [bold]{suppliers}[/] of them.\n\n" +
                    "[dim]Every statement above carries the other side's keys, so the service filters\n" +
                    "the container instead of handing it over to be filtered here.[/]"))
                .Header("[teal]What it cost[/]")
                .BorderColor(Color.Teal)
                .Padding(1, 0));

            return 0;
        }

        /// <summary>
        /// Runs one query, showing its rows and whatever it caused Cosmos to be asked.
        /// </summary>
        static async Task Section(DbConnection connection, CosmosWatcher watcher, string title, string sql)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule($"[bold teal]{title}[/]").LeftJustified().RuleStyle("grey"));
            AnsiConsole.WriteLine();

            AnsiConsole.Write(new Panel(new Markup($"[silver]{Markup.Escape(sql.Trim())}[/]"))
                .Border(BoxBorder.None)
                .Padding(2, 0));

            AnsiConsole.WriteLine();

            watcher.Clear();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            var table = new Table()
                .Border(TableBorder.Rounded)
                .BorderColor(Color.Grey);

            var count = 0;

            await using (var reader = await command.ExecuteReaderAsync())
            {
                for (var i = 0; i < reader.FieldCount; i++)
                    table.AddColumn(new TableColumn($"[bold]{Markup.Escape(reader.GetName(i))}[/]"));

                while (await reader.ReadAsync())
                {
                    var row = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        row[i] = reader.IsDBNull(i) ? "[dim]∅[/]" : Markup.Escape(reader.GetValue(i)?.ToString() ?? "");

                    table.AddRow(row);
                    count++;
                }
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine("  [dim]{0} row(s)[/]", count);

            foreach (var statement in watcher.Drain())
            {
                AnsiConsole.WriteLine();
                AnsiConsole.Write(new Panel(new Markup($"[green]{Markup.Escape(Abbreviate(statement))}[/]"))
                    .Header("[dim]sent to Cosmos[/]")
                    .BorderColor(Color.Grey35)
                    .Padding(1, 0));
            }
        }

        /// <summary>
        /// Collapses the lookup join's key parameters, which are numerous and all alike.
        /// </summary>
        /// <remarks>
        /// The statement carries one parameter per batch slot because it is rendered once and run per
        /// batch. A hundred of them says nothing a reader does not learn from the first and the last,
        /// and the count is kept so the abbreviation is not mistaken for the statement.
        /// </remarks>
        static string Abbreviate(string sql)
        {
            return Regex.Replace(sql, @"@k0(, @k\d+){3,}", match =>
            {
                var count = match.Value.Split(',').Length;
                return $"@k0, … , @k{count - 1}   /* {count} keys */";
            });
        }

        /// <summary>
        /// Two adapters and a view over both.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SUPPLIERS</c> is Calcite's CSV adapter over a directory and <c>COSMOS</c> is this adapter
        /// over the emulator. <c>SALES</c> is an ordinary schema holding nothing but a view, which is
        /// where the two meet — a caller querying <c>PRODUCT_SUPPLIERS</c> never names either source.
        /// </para>
        /// <para>
        /// <b>Both factories are named assembly-qualified, and that is not optional.</b> The names are
        /// resolved through IKVM, where a bare namespace-qualified name finds nothing — for the .NET
        /// factory and for the Java one alike, since the CSV adapter lives in its own assembly rather
        /// than in calcite-core.
        /// </para>
        /// <para>
        /// The join is on the product id rather than the category, and that is not incidental. The id
        /// is a typed column on both sides, so the adapter can bind it as a parameter and fetch by it;
        /// a Cosmos partition key column is typed as <c>ANY</c>, which says nothing about what would be
        /// bound, and the lookup is declined rather than guessed at.
        /// </para>
        /// </remarks>
        static string Model => $$"""
        {
          "version": "1.0",
          "defaultSchema": "SALES",
          "schemas": [
            {
              "name": "SUPPLIERS",
              "type": "custom",
              "factory": "org.apache.calcite.adapter.csv.CsvSchemaFactory, calcite.csv",
              "operand": { "directory": "{{Sources.CsvDirectory.Replace("\\", "\\\\")}}" }
            },
            {
              "name": "COSMOS",
              "type": "custom",
              "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter",
              "operand": {
                "endpoint": "{{Sources.CosmosEndpoint}}",
                "key": "{{Sources.CosmosKey}}",
                "database": "{{Sources.Database}}",
                "containers": [ "products" ],
                "connectionMode": "gateway"
              }
            },
            {
              "name": "SALES",
              "tables": [
                {
                  "name": "PRODUCT_SUPPLIERS",
                  "type": "view",
                  "sql": [
                    "SELECT p.\"id\" AS \"PRODUCT\", p.\"category\" AS \"CATEGORY\",",
                    "       CAST(p.\"_MAP\"['name'] AS VARCHAR) AS \"NAME\",",
                    "       CAST(p.\"_MAP\"['price'] AS INTEGER) AS \"PRICE\",",
                    "       s.\"SUPPLIER\", s.\"LEAD_DAYS\"",
                    "FROM \"COSMOS\".\"products\" AS p",
                    "JOIN \"SUPPLIERS\".\"SUPPLIERS\" AS s ON p.\"id\" = s.\"PRODUCT\""
                  ]
                }
              ]
            }
          ]
        }
        """;

    }

    /// <summary>
    /// Listens to what the Cosmos adapter reports about the requests it makes.
    /// </summary>
    /// <remarks>
    /// The adapter publishes a <see cref="Meter"/> and an <see cref="ActivitySource"/> under its own
    /// name, so this needs no hook into the plan and no cooperation from the adapter beyond collecting
    /// what any .NET application already can.
    /// </remarks>
    sealed class CosmosWatcher : IDisposable
    {

        readonly List<string> _statements = new();
        readonly MeterListener _meter = new();
        readonly ActivityListener _activity;

        public CosmosWatcher()
        {
            _meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CosmosInstrumentation.Name && instrument.Name == "cosmos.request_charge")
                    listener.EnableMeasurementEvents(instrument);
            };

            _meter.SetMeasurementEventCallback<double>((_, value, _, _) =>
            {
                lock (_statements)
                    Charge += value;
            });

            _meter.Start();

            _activity = new ActivityListener
            {
                ShouldListenTo = source => source.Name == CosmosInstrumentation.Name,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.GetTagItem("db.query.text") is string text)
                        lock (_statements)
                        {
                            _statements.Add(text);
                            Statements++;
                        }
                },
            };

            ActivitySource.AddActivityListener(_activity);
        }

        public double Charge { get; private set; }

        public int Statements { get; private set; }

        public void Clear()
        {
            lock (_statements)
                _statements.Clear();
        }

        public IReadOnlyList<string> Drain()
        {
            lock (_statements)
            {
                var copy = _statements.ToArray();
                _statements.Clear();
                return copy;
            }
        }

        public void Dispose()
        {
            _meter.Dispose();
            _activity.Dispose();
        }

    }

}
