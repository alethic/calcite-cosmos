using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;
using Apache.Calcite.Extensions.Adapter.Enumerable;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite;
using org.apache.calcite.adapter.java;
using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// Checks every pushdown against an oracle: the same SQL planned with the full rule set and with
    /// only the way-out converter, both executed against the same live container, rows required
    /// equal.
    /// </summary>
    /// <remarks>
    /// See <c>DESIGN.md</c> under <em>Differential testing</em>. The oracle is the adapter's own
    /// minimal mode — the scan read whole, Calcite evaluating everything in process — so a mismatch
    /// indicts the pushdown rather than the plumbing around it. The corpus leans into the semantics
    /// that have bitten: null against absent, <c>NOT</c> over both, grouping by a key some documents
    /// lack, <c>LIKE</c>'s shapes, and the aggregate forms.
    /// </remarks>
    [TestClass]
    public class CosmosDifferentialTests
    {

        // Well-known public emulator credentials, documented by Microsoft. Not a secret.
        const string EmulatorEndpoint = "http://localhost:8081/";
        const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        static readonly string Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") is string e && e.Length > 0 ? e : EmulatorEndpoint;
        static readonly string Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY") is string k && k.Length > 0 ? k : EmulatorKey;
        static bool IsEmulator => ReferenceEquals(Endpoint, EmulatorEndpoint);

        static readonly string DatabaseName = "calcite_cosmos_diff_" +
            System.Text.RegularExpressions.Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        /// <summary>
        /// The documents both plans read: prices present, null and absent; a document with a null
        /// category and one with none, which land in different groups than either expects; names for
        /// the LIKE shapes; a nested object and arrays.
        /// </summary>
        static readonly string[] Documents =
        [
            """{"id":"1","category":"bikes","name":"Trail Blazer","price":120,"tags":["outdoor","steel"]}""",
            """{"id":"2","category":"bikes","name":"Road Runner","price":340,"metadata":{"sku":"B-2"}}""",
            """{"id":"3","category":"shoes","name":"Sprint","price":80}""",
            """{"id":"4","category":"shoes","name":"Marathon","price":null}""",
            """{"id":"5","category":"shoes","name":"Slipper"}""",
            """{"id":"6","category":null,"name":"Uncategorized","price":5}""",
            """{"id":"7","name":"Unfiled","price":10}""",
        ];

        static CosmosClient? _client;
        static Container? _container;
        static string? _initializationFailure;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(IsEmulator ? 5 : 30),
                MaxRetryAttemptsOnRateLimitedRequests = 0,
            };

            if (IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(IsEmulator ? 10 : 120));
                var client = new CosmosClient(Endpoint, Key, options);

                var database = (await client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;

                try { await database.GetContainer("products").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("products", "/category"), cancellationToken: cts.Token)).Container;

                foreach (var json in Documents)
                {
                    using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var document = System.Text.Json.JsonDocument.Parse(json);

                    var partitionKey = document.RootElement.TryGetProperty("category", out var category)
                        ? category.ValueKind == System.Text.Json.JsonValueKind.Null ? PartitionKey.Null : new PartitionKey(category.GetString())
                        : PartitionKey.None;

                    using var response = await container.CreateItemStreamAsync(stream, partitionKey, cancellationToken: cts.Token);
                    response.EnsureSuccessStatusCode();
                }

                _client = client;
                _container = container;
            }
            catch (Exception e)
            {
                _initializationFailure = e.ToString();
                _client?.Dispose();
                _client = null;
                _container = null;
            }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            _client?.Dispose();
            _client = null;
            _container = null;
        }

        sealed class TestDataContext : DataContext
        {

            readonly SchemaPlus _rootSchema;
            readonly JavaTypeFactory _typeFactory;

            public TestDataContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory)
            {
                _rootSchema = rootSchema;
                _typeFactory = typeFactory;
            }

            public SchemaPlus getRootSchema() => _rootSchema;

            public JavaTypeFactory getTypeFactory() => _typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => null!;

        }

        /// <summary>
        /// Plans and executes a statement, with the pushdown rules or with only the way out.
        /// </summary>
        static async Task<List<object>> Run(string sql, bool pushdown)
        {
            if (_container is null)
                Assert.Inconclusive("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(Products, new CosmosQueryExecutor(_container!));

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var validator = SqlValidatorUtil.newValidator(SqlStdOperatorTable.instance(), catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            if (pushdown)
            {
                foreach (var rule in CosmosRules.GetRules(table.Convention))
                    planner.addRule(rule);
            }
            else
            {
                // The oracle: the scan is readable and nothing else is pushed, so Calcite evaluates
                // everything in process over the whole container.
                planner.addRule(CosmosToClrAsyncEnumerableConverterRule.Create(table.Convention));
            }

            foreach (var rule in ClrAsyncEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            var best = planner.findBestExp();

            var program = new org.apache.calcite.plan.hep.HepProgramBuilder();
            foreach (var rule in ClrAsyncEnumerableRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
            hep.setRoot(best);
            best = hep.findBestExp();

            var implementor = new ClrAsyncEnumerableRelImplementor(best.getCluster().getRexBuilder(), new java.util.HashMap());
            var lambda = implementor.ImplementRoot((ClrAsyncEnumerableRel)best, ClrEnumerablePrefer.Array);

            var run = (Func<DataContext, IAsyncEnumerable<object>>)lambda.Compile();
            var context = new TestDataContext(rootSchema.plus(), typeFactory);

            var rows = new List<object>();
            await foreach (var row in run(context))
                rows.Add(row);

            return rows;
        }

        /// <summary>
        /// Reduces a row to a canonical text, so that two boxes meaning the same value compare equal
        /// and a document's entry order means nothing.
        /// </summary>
        static string Canonical(object? value)
        {
            switch (value)
            {
                case null:
                    return "null";

                case object[] row:
                    return "(" + string.Join(", ", row.Select(Canonical)) + ")";

                case string s:
                    return "\"" + s + "\"";

                case bool b:
                    return b ? "true" : "false";
                case java.lang.Boolean jb:
                    return jb.booleanValue() ? "true" : "false";

                case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                    return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
                case java.lang.Number number:
                    return number.doubleValue().ToString("R", CultureInfo.InvariantCulture);

                case java.util.Map map:
                    {
                        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal);
                        var iterator = map.entrySet().iterator();
                        while (iterator.hasNext())
                        {
                            var entry = (java.util.Map.Entry)iterator.next();
                            entries[entry.getKey()?.ToString() ?? "null"] = Canonical(entry.getValue());
                        }

                        return "{" + string.Join(", ", entries.Select(e => e.Key + ": " + e.Value)) + "}";
                    }

                case java.util.List list:
                    {
                        var items = new List<string>();
                        for (var i = 0; i < list.size(); i++)
                            items.Add(Canonical(list.get(i)));

                        return "[" + string.Join(", ", items) + "]";
                    }

                default:
                    return value.GetType().Name + ":" + value;
            }
        }

        /// <summary>
        /// Runs one statement both ways and describes the difference, or returns <c>null</c> where
        /// there is none.
        /// </summary>
        static async Task<string?> Compare(string sql, bool ordered)
        {
            List<string> pushed;
            List<string> oracle;

            try
            {
                pushed = (await Run(sql, pushdown: true)).Select(Canonical).ToList();
                oracle = (await Run(sql, pushdown: false)).Select(Canonical).ToList();
            }
            catch (Exception e)
            {
                return $"{sql}\n  failed to run: {e.Message}";
            }

            if (ordered == false)
            {
                pushed.Sort(StringComparer.Ordinal);
                oracle.Sort(StringComparer.Ordinal);
            }

            if (pushed.SequenceEqual(oracle))
                return null;

            return $"{sql}\n  pushed: [{string.Join("; ", pushed)}]\n  oracle: [{string.Join("; ", oracle)}]";
        }

        /// <summary>
        /// The corpus. Ordered statements compare as sequences; the rest as multisets.
        /// </summary>
        /// <remarks>
        /// Excluded by name, with the recorded reason: out-of-domain arithmetic (<c>SQRT(-1)</c> and
        /// kin), which is pushed deliberately and diverges deliberately — the service fails the
        /// query where Calcite yields NaN; see <c>DESIGN.md</c>.
        /// </remarks>
        static readonly (string Sql, bool Ordered)[] Corpus =
        [
            // Projections.
            ("SELECT * FROM products", false),
            ("SELECT c.\"id\", c.\"category\" FROM products AS c", false),
            ("SELECT c.\"_MAP\"['metadata']['sku'] FROM products AS c", false),
            ("SELECT c.\"_MAP\"['price'] FROM products AS c", false),

            // Filters: comparisons, null against absent, NOT over both, disjunction, LIKE's shapes.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" = 'bikes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] > 50", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] IS NULL", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] IS NOT NULL", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" IS NULL", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"category\" = 'bikes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" = 'bikes' OR c.\"category\" = 'shoes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" IN ('bikes', 'shoes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] BETWEEN 10 AND 200", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"id\" = '3' AND c.\"category\" = 'shoes'", false),

            // Sorts and row restrictions, ordered by the unique id so the comparison is meaningful.
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\"", true),
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\" OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY", true),

            // Aggregates: the forms, and grouping by a key some documents lack.
            ("SELECT COUNT(*) FROM products", false),
            ("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY c.\"category\"", false),
            ("SELECT MIN(c.\"_ts\"), MAX(c.\"_ts\") FROM products AS c", false),
            ("SELECT SUM(c.\"_ts\") FROM products AS c", false),
            ("SELECT COUNT(DISTINCT c.\"category\") FROM products AS c", false),
            ("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY ROLLUP(c.\"category\")", false),
            ("SELECT c.\"category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"category\" HAVING c.\"category\" = 'bikes'", false),
            ("SELECT AVG(c.\"_ts\") FROM products AS c", false),

            // LIKE's shapes: the prefix that becomes STARTSWITH, and the general form that stays LIKE.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['name'] LIKE 'S%'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['name'] LIKE '%Runner%'", false),

            // Array traversal.
            ("SELECT c.\"id\" FROM products AS c, UNNEST(c.\"_MAP\"['tags']) AS t", false),
        ];

        [TestMethod]
        public async Task EveryStatementAgreesWithTheOracle()
        {
            var failures = new List<string>();

            foreach (var (sql, ordered) in Corpus)
                if (await Compare(sql, ordered) is string failure)
                    failures.Add(failure);

            failures.Should().BeEmpty("every pushdown must answer as Calcite would:\n" + string.Join("\n", failures));
        }

    }

}
