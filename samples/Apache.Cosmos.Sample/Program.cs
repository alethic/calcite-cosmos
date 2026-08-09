using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Data;

namespace Apache.Cosmos.Sample
{

    /// <summary>
    /// Joins a Cosmos DB container to a SQL Server table, through a view, in one SQL statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cosmos has no relational join — its <c>JOIN</c> cross-products a document with its own nested
    /// arrays — so a query spanning it and anything else has to be joined outside the service. The
    /// interesting part is what that costs: the adapter takes the join keys from the SQL side and
    /// sends them with the statement, so only the documents that could match come back.
    /// </para>
    /// <para>
    /// The sample prints every statement Cosmos was given, so the difference is visible rather than
    /// claimed.
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
                Console.WriteLine();
                Console.WriteLine("Failed:");

                for (var cause = e; cause is not null; cause = Next(cause))
                    Console.WriteLine("  " + cause.GetType().Name + ": " + FirstLine(cause.Message));

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
            Console.WriteLine("Apache Calcite — two adapters, one join");
            Console.WriteLine(new string('=', 64));
            Console.WriteLine();

            if (await Sources.WhatIsMissingAsync() is string missing)
            {
                Console.WriteLine(missing);
                return 1;
            }

            // A model names its schema factories by type name, resolved through IKVM. A .NET type
            // needs the assembly-qualified form -- the bare namespace-qualified name does not resolve,
            // and neither does the cli.-prefixed one Calcite's own loader would otherwise find.
            // And a factory is only found if its assembly is loaded, which for a name that appears
            // nowhere but in a JSON string means touching it. A discarded typeof() is not enough — the
            // compiler can elide it — so both are reached through something with a runtime effect.
            _ = new Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory();
            _ = org.apache.calcite.adapter.csv.CsvSchemaFactory.INSTANCE;

            var suppliers = Sources.EnsureCsv();
            var documents = await Sources.EnsureCosmosAsync();

            Console.WriteLine($"CSV       : {Sources.CsvDirectory} — {suppliers} supplier rows");
            Console.WriteLine($"Cosmos DB : {Sources.Database}/products — {documents} documents");
            Console.WriteLine();

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
                "The view, joined across both engines",
                """SELECT * FROM "SALES"."PRODUCT_SUPPLIERS" ORDER BY "PRODUCT" """);

            await Section(connection, watcher,
                "A predicate on the Cosmos side, pushed into the statement",
                """SELECT "PRODUCT", "SUPPLIER", "PRICE" FROM "SALES"."PRODUCT_SUPPLIERS" WHERE "PRICE" > 1000""");

            await Section(connection, watcher,
                "An aggregate, grouped by a column each side contributes to",
                """SELECT "SUPPLIER", COUNT(*) AS "LINES", SUM("PRICE") AS "TOTAL" FROM "SALES"."PRODUCT_SUPPLIERS" GROUP BY "SUPPLIER" ORDER BY "SUPPLIER" """);

            Console.WriteLine();
            Console.WriteLine($"Cosmos was asked {watcher.Statements} time(s) for {watcher.Charge:0.##} RU in total.");
            Console.WriteLine("The container holds six documents; the supplier table names three of them.");

            return 0;
        }

        /// <summary>
        /// Runs one query, printing its rows and whatever it caused Cosmos to be asked.
        /// </summary>
        static async Task Section(DbConnection connection, CosmosWatcher watcher, string title, string sql)
        {
            Console.WriteLine(title);
            Console.WriteLine(new string('-', title.Length));
            Console.WriteLine(sql.Trim());
            Console.WriteLine();

            watcher.Clear();

            await using var command = connection.CreateCommand();
            command.CommandText = sql;

            await using (var reader = await command.ExecuteReaderAsync())
            {
                var widths = new int[reader.FieldCount];
                var rows = new List<string[]>();
                var header = new string[reader.FieldCount];

                for (var i = 0; i < reader.FieldCount; i++)
                {
                    header[i] = reader.GetName(i);
                    widths[i] = header[i].Length;
                }

                while (await reader.ReadAsync())
                {
                    var row = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        row[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i)?.ToString() ?? "";
                        widths[i] = Math.Max(widths[i], row[i].Length);
                    }

                    rows.Add(row);
                }

                Console.WriteLine("  " + Row(header, widths));
                Console.WriteLine("  " + Rule(widths));

                foreach (var row in rows)
                    Console.WriteLine("  " + Row(row, widths));

                Console.WriteLine();
                Console.WriteLine($"  {rows.Count} row(s)");
            }

            foreach (var statement in watcher.Drain())
                Console.WriteLine("  cosmos ▸ " + statement);

            Console.WriteLine();
        }

        static string Row(string[] values, int[] widths)
        {
            var parts = new string[values.Length];
            for (var i = 0; i < values.Length; i++)
                parts[i] = values[i].PadRight(widths[i]);

            return string.Join("  ", parts);
        }

        static string Rule(int[] widths)
        {
            var parts = new string[widths.Length];
            for (var i = 0; i < widths.Length; i++)
                parts[i] = new string('-', widths[i]);

            return string.Join("  ", parts);
        }

        /// <summary>
        /// Two adapters and a view over both.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SQL</c> reaches SQL Server through ADO.NET and <c>COSMOS</c> reaches the emulator.
        /// <c>SALES</c> is an ordinary schema holding nothing but a view, which is where the two meet —
        /// a caller querying <c>PRODUCT_SUPPLIERS</c> never names either source.
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
