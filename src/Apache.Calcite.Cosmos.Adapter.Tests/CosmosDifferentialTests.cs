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
        /// A second container, seeded so that the declared type of a cast column is a lie for some of
        /// its documents.
        /// </summary>
        /// <remarks>
        /// Separate from <c>products</c> deliberately. A path holding a number in one document and a
        /// string in the next is exactly what the rest of the corpus must not carry: an ordinary
        /// comparison over such a path asks a question neither side is obliged to answer the same way,
        /// and the divergence it produced would say nothing about casts. Keeping the hazard here means
        /// only the statements that name it read it.
        /// </remarks>
        static readonly CosmosContainerMetadata Typed = new("typed", new[] { "/category" });

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

        /// <summary>
        /// The documents a typed view lies about: a number of the declared type, a string where the
        /// declaration says integer, a fractional value where it says integer, the property absent, and
        /// the property present and null.
        /// <para>
        /// <c>label</c> holds a different JSON type in every document — a string, a number, a boolean,
        /// an array, an object, null, and nothing at all — because rendering a value as text is asked
        /// of whatever is there, and the claim that only a stored string can render as <c>'bikes'</c>
        /// is a claim about all seven.
        /// </para>
        /// <para>
        /// The partitions are <c>a</c>, <c>b</c>, and <c>30</c>. The last is a partition key that looks
        /// like a number while being a string, which is the case that separates a literal safe to route
        /// on from one that is not.
        /// </para>
        /// </summary>
        static readonly string[] TypedDocuments =
        [
            """{"id":"1","category":"a","name":"Exact","price":30,"label":"bikes"}""",
            """{"id":"2","category":"a","name":"Stringy","price":"30","label":30}""",
            """{"id":"3","category":"a","name":"Fractional","price":30.7,"label":true}""",
            """{"id":"4","category":"a","name":"Absent","label":["bikes"]}""",
            """{"id":"5","category":"a","name":"Null","price":null,"label":{"v":"bikes"}}""",
            """{"id":"6","category":"b","name":"Other","price":7,"label":null}""",
            """{"id":"7","category":"b","name":"NoLabel","price":8}""",
            """{"id":"8","category":"30","name":"NumericLookingKey","price":9,"label":"shoes"}""",
            """{"id":"9","category":"a","name":"Huge","price":9,"label":"huge","big":1e30}""",
        ];

        static CosmosClient? _client;
        static Container? _container;
        static Container? _typedContainer;
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

                _container = await Seed(database, "products", Documents, cts.Token);
                _typedContainer = await Seed(database, "typed", TypedDocuments, cts.Token);

                _client = client;
            }
            catch (Exception e)
            {
                _initializationFailure = e.ToString();
                _client?.Dispose();
                _client = null;
                _container = null;
                _typedContainer = null;
            }
        }

        /// <summary>
        /// Recreates a container and writes the given documents into it.
        /// </summary>
        static async Task<Container> Seed(Database database, string name, string[] documents, CancellationToken cancellationToken)
        {
            try { await database.GetContainer(name).DeleteContainerAsync(cancellationToken: cancellationToken); } catch (CosmosException) { }
            var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties(name, "/category"), cancellationToken: cancellationToken)).Container;

            foreach (var json in documents)
            {
                using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));
                using var document = System.Text.Json.JsonDocument.Parse(json);

                var partitionKey = document.RootElement.TryGetProperty("category", out var category)
                    ? category.ValueKind == System.Text.Json.JsonValueKind.Null ? PartitionKey.Null : new PartitionKey(category.GetString())
                    : PartitionKey.None;

                using var response = await container.CreateItemStreamAsync(stream, partitionKey, cancellationToken: cancellationToken);
                response.EnsureSuccessStatusCode();
            }

            return container;
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

            _client?.Dispose();
            _client = null;
            _container = null;
            _typedContainer = null;
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
            var typeFactory = new JavaTypeFactoryImpl();
            var table = new CosmosTable(Products, new CosmosQueryExecutor(_container!));
            var typed = new CosmosTable(Typed, new CosmosQueryExecutor(_typedContainer!));

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", table);
            rootSchema.add("typed", typed);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            // Chained with the library table so the corpus can name LEFT, RIGHT, REVERSE and REPEAT
            // — Calcite's standard table has none of them, and they are the functions whose two
            // dialects this suite exists to compare.
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(),
                org.apache.calcite.sql.fun.SqlLibraryOperatorTableFactory.INSTANCE.getOperatorTable(
                    java.util.EnumSet.of(
                        org.apache.calcite.sql.fun.SqlLibrary.MYSQL,
                        org.apache.calcite.sql.fun.SqlLibrary.SPARK,
                        org.apache.calcite.sql.fun.SqlLibrary.HIVE,
                        org.apache.calcite.sql.fun.SqlLibrary.BIG_QUERY)));

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            // A convention per container, so both tables need their rules registered whichever a
            // statement happens to name.
            var pushdownRules = new List<string>();

            foreach (var convention in new[] { table.Convention, typed.Convention })
            {
                foreach (var rule in CosmosRules.GetRules(convention))
                {
                    planner.addRule(rule);

                    if (IsPushdown(rule))
                        pushdownRules.Add(java.util.regex.Pattern.quote(rule.ToString()));
                }
            }

            // The oracle: nothing is pushed, so Calcite reads the container whole and evaluates
            // everything in process.
            //
            // Excluded rather than never added, because never adding them does not keep them out. A
            // convention registers its own rules -- Convention.register, which a Volcano planner calls
            // the first time it sees a node carrying one -- so a scan arriving in the Cosmos convention
            // brings the whole pushdown set with it however the planner was built. Adding only the way
            // out therefore built no oracle at all: the two runs planned identically, and every
            // statement in the corpus agreed with itself.
            //
            // Removing them again does not work either, and the reason says why this is the lever: the
            // planner queues a rule's matches when the root is registered, so by the first moment the
            // rules provably exist the matches that fire them are already waiting. An exclusion filter
            // is read when a match fires rather than when it is queued, and is set before either.
            if (pushdown == false)
                planner.setRuleDescExclusionFilter(java.util.regex.Pattern.compile(string.Join("|", pushdownRules)));

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
        /// Determines whether a rule is one the oracle must not have.
        /// </summary>
        /// <remarks>
        /// Everything that moves work to the service. Not the way out, which is what makes a pushed
        /// subtree readable at all and which the oracle needs for the scan; and not Calcite's own
        /// rewrites, which the convention registers because a bare Volcano planner has no logical rule
        /// set -- they preserve meaning, and one of them is what makes a grouping-set AVG planable by
        /// the asynchronous convention in the first place.
        /// </remarks>
        static bool IsPushdown(org.apache.calcite.plan.RelOptRule rule)
        {
            return rule is CosmosAggregateRule or CosmosAggregateSplitRule
                or CosmosFilterRule or CosmosFilterSplitRule
                or CosmosProjectRule or CosmosRankRule or CosmosSortRule
                or CosmosUnnestRule or CosmosLookupJoinRule;
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
        /// <remarks>
        /// Which side failed is part of the answer, not an incidental detail of the message: a pushdown
        /// that throws where the oracle returns rows is a different defect from one that returns the
        /// wrong rows, and the two were indistinguishable while both were reported as "failed to run".
        /// </remarks>
        static async Task<string?> Compare(string sql, bool ordered)
        {
            List<string> pushed;
            List<string> oracle;

            try { oracle = (await Run(sql, pushdown: false)).Select(Canonical).ToList(); }
            catch (Exception e) when (e is not AssertInconclusiveException) { return $"{sql}\n  the oracle failed to run: {e.Message}"; }

            try { pushed = (await Run(sql, pushdown: true)).Select(Canonical).ToList(); }
            catch (Exception e) when (e is not AssertInconclusiveException) { return $"{sql}\n  the pushdown failed to run: {e.Message}\n  oracle: [{string.Join("; ", oracle)}]"; }

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
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" = 'bikes' OR c.\"category\" = 'shoes'", false),

            // Negation over a path that is null in one document and absent in another. The service's
            // equality over a null is false where SQL's is unknown, and only the negation tells them
            // apart -- in a positive position false and unknown both discard the row.
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"category\" = 'bikes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" <> 'bikes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"category\" <> 'bikes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"_MAP\"['price'] > 50)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (UPPER(CAST(c.\"category\" AS VARCHAR)) = 'BIKES')", false),

            // The shapes the guard is deliberately not applied to, because applying it would be too
            // strong. Here to say whether leaving them alone is right.
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"category\" = 'bikes' AND c.\"_MAP\"['price'] > 50)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"category\" = 'bikes' OR c.\"_MAP\"['price'] > 50)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (NOT (c.\"category\" = 'bikes'))", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" IN ('bikes', 'shoes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] BETWEEN 10 AND 200", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"id\" = '3' AND c.\"category\" = 'shoes'", false),

            // Sorts and row restrictions, ordered by the unique id so the comparison is meaningful.
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\"", true),
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\" OFFSET 2 ROWS FETCH NEXT 3 ROWS ONLY", true),

            // Aggregates: the forms, and grouping by a key some documents lack.
            ("SELECT COUNT(*) FROM products", false),
            ("SELECT MIN(c.\"_ts\"), MAX(c.\"_ts\") FROM products AS c", false),

            // Over _ts rather than a user path, and that is a limit rather than a choice. MIN, MAX,
            // SUM and AVG over an ANY column have no implementation in the asynchronous convention,
            // and the aggregate rule declines to push one -- so a statement naming any of them forms
            // no plan in either mode and cannot be compared or even run. Measured, on all four.
            // Nothing about pushdown; there is simply nothing to execute. Written down because the
            // absence of these from a corpus about null and absent values otherwise reads as an
            // oversight, and because the day the convention grows them is the day they belong here.
            ("SELECT SUM(c.\"_ts\") FROM products AS c", false),
            ("SELECT COUNT(DISTINCT c.\"category\") FROM products AS c", false),
            ("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY c.\"category\"", false),
            ("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY ROLLUP(c.\"category\")", false),
            ("SELECT c.\"category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"category\" HAVING c.\"category\" = 'bikes'", false),
            ("SELECT AVG(c.\"_ts\") FROM products AS c", false),

            // LIKE's shapes: the prefix that becomes STARTSWITH, and the general form that stays LIKE.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['name'] LIKE 'S%'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['name'] LIKE '%Runner%'", false),

            // Array traversal.

            // The string functions mapped from a SQL counterpart, which is exactly where the two
            // could disagree — the oracle evaluates SQL's, the pushdown Cosmos's.
            ("SELECT LEFT(CAST(c.\"_MAP\"['name'] AS VARCHAR), 3) FROM products AS c", false),
            ("SELECT RIGHT(CAST(c.\"_MAP\"['name'] AS VARCHAR), 3) FROM products AS c", false),
            ("SELECT REVERSE(CAST(c.\"_MAP\"['name'] AS VARCHAR)) FROM products AS c", false),
            ("SELECT REPEAT(CAST(c.\"_MAP\"['name'] AS VARCHAR), 2) FROM products AS c", false),

            // And the boundary that most often differs between dialects: a count longer than the
            // string, which both are documented to clamp rather than fail.
            ("SELECT LEFT(CAST(c.\"_MAP\"['name'] AS VARCHAR), 99) FROM products AS c", false),
            ("SELECT RIGHT(CAST(c.\"_MAP\"['name'] AS VARCHAR), 99) FROM products AS c", false),

            // The array functions, and the index shift above all: the oracle counts from one and
            // the pushdown from zero, so an off-by-one in the translation shows here as different
            // elements rather than as an error.
            ("SELECT ARRAY_SLICE(c.\"_MAP\"['tags'], 0, 1) FROM products AS c WHERE c.\"id\" = '1'", false),
            ("SELECT ARRAY_SLICE(c.\"_MAP\"['tags'], 1, 1) FROM products AS c WHERE c.\"id\" = '1'", false),
            ("SELECT ARRAY_SLICE(c.\"_MAP\"['tags'], 2, 1) FROM products AS c WHERE c.\"id\" = '1'", false),
            ("SELECT ARRAY_SLICE(c.\"_MAP\"['tags'], 0, 2) FROM products AS c WHERE c.\"id\" = '1'", false),

            // SUBSTRING carries the same adjustment ARRAY_SLICE carried wrongly, and SQL's origin
            // really is one here. Covered so that the two are not assumed to be the same question.
            ("SELECT SUBSTRING(CAST(c.\"_MAP\"['name'] AS VARCHAR) FROM 1 FOR 3) FROM products AS c", false),
            ("SELECT SUBSTRING(CAST(c.\"_MAP\"['name'] AS VARCHAR) FROM 2 FOR 3) FROM products AS c", false),

            ("SELECT ARRAY_UNION(c.\"_MAP\"['tags'], c.\"_MAP\"['tags']) FROM products AS c WHERE c.\"id\" = '1'", false),
            ("SELECT ARRAY_INTERSECT(c.\"_MAP\"['tags'], c.\"_MAP\"['tags']) FROM products AS c WHERE c.\"id\" = '1'", false),

            // Not here, and the absences are facts rather than oversights: the library's
            // ARRAY_SLICE takes exactly three arguments, so Cosmos's two-argument form has no SQL
            // spelling to compare against; and ARRAY_CONCAT's operand checker refuses a map value,
            // which is typed ANY, so the statement does not validate over this row model at all.

            // DISTINCT, which the adapter now emits as the keyword rather than as a GROUP BY over
            // every key. The seeded documents include a null category and an absent one, so this
            // asks the question that matters: whether the service's dedup agrees with SQL's about
            // null and undefined.
            ("SELECT DISTINCT c.\"category\" FROM products AS c", false),
            ("SELECT DISTINCT c.\"category\", c.\"id\" FROM products AS c", false),
            ("SELECT DISTINCT c.\"_ts\" FROM products AS c ORDER BY c.\"_ts\"", true),


            // ── A sweep over the surfaces that carried no statement at all ─────────
            //
            // Every one of these reads a path that is null in one document and absent in another,
            // because that is where this adapter has been wrong every time so far.

            // Comparison and range, in both positions.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] < 100", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] >= 120", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"_MAP\"['price'] BETWEEN 10 AND 200)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] NOT IN (120, 340)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" NOT IN ('bikes', 'shoes')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"category\" IN ('bikes', 'shoes'))", false),

            // LIKE and its negation over a path some documents lack.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['name'] NOT LIKE 'S%'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"_MAP\"['name'] LIKE '%Runner%')", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['name'] LIKE '_prin_'", false),

            // Arithmetic over a null and an absent operand, which SQL makes unknown.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] + 1 > 100", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"_MAP\"['price'] + 1 > 100)", false),
            ("SELECT c.\"_MAP\"['price'] + 1 FROM products AS c", false),

            // Conjunction and disjunction mixing a known-true arm with an unknown one.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] > 50 OR c.\"category\" = 'shoes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] > 50 AND c.\"category\" = 'shoes'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"_MAP\"['price'] IS NULL)", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"_MAP\"['price'] IS NOT NULL)", false),

            // Aggregates over a column that is null in one document, absent in another, and a
            // string in none -- what SUM and AVG skip is not obviously the same on both sides.
            ("SELECT COUNT(c.\"_MAP\"['price']) FROM products AS c", false),
            ("SELECT COUNT(c.\"category\") FROM products AS c", false),
            ("SELECT c.\"category\", COUNT(*) FROM products AS c WHERE c.\"_MAP\"['price'] IS NOT NULL GROUP BY c.\"category\"", false),
            ("SELECT COUNT(DISTINCT c.\"_MAP\"['name']) FROM products AS c", false),

            // Ordering, where the placement of a null and an absent value is the whole question.
            ("SELECT c.\"id\", c.\"category\" FROM products AS c ORDER BY c.\"id\" DESC", true),
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\" FETCH NEXT 3 ROWS ONLY", true),
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"id\" OFFSET 6 ROWS", true),

            // The string functions over a path some documents lack, which is where a service
            // function returning undefined and SQL returning null could part company.
            ("SELECT c.\"id\", UPPER(CAST(c.\"_MAP\"['name'] AS VARCHAR)) FROM products AS c", false),
            ("SELECT c.\"id\", CHAR_LENGTH(CAST(c.\"_MAP\"['name'] AS VARCHAR)) FROM products AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE CHAR_LENGTH(CAST(c.\"_MAP\"['name'] AS VARCHAR)) > 6", false),

            // Nested and array-valued paths, read where they are absent.
            ("SELECT c.\"id\", c.\"_MAP\"['metadata']['sku'] FROM products AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['metadata']['sku'] = 'B-2'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"_MAP\"['metadata']['sku'] = 'B-2')", false),
            ("SELECT c.\"id\", c.\"_MAP\"['tags'][0] FROM products AS c", false),
            ("SELECT c.\"id\", c.\"_MAP\"['tags'][1] FROM products AS c", false),
            ("SELECT c.\"id\", c.\"_MAP\"['tags'][2] FROM products AS c", false),
            ("SELECT c.\"id\", c.\"_MAP\"['tags'][3] FROM products AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['tags'][1] = 'outdoor'", false),
            ("SELECT c.\"id\" FROM products AS c WHERE CARDINALITY(c.\"_MAP\"['tags']) = 2", false),

            // CASE, whose arms are where an unknown condition goes somewhere visible.
            ("SELECT c.\"id\", CASE WHEN c.\"category\" = 'bikes' THEN 1 ELSE 0 END FROM products AS c", false),
            ("SELECT c.\"id\" FROM products AS c WHERE (CASE WHEN c.\"category\" = 'bikes' THEN 1 ELSE 0 END) = 0", false),


            // ── A second sweep: ordering, aggregation and row restriction over a path
            //    that is null in one document and absent in another ─────────────────

            // Ordering by a nullable user path, both directions and both null placements. Where the
            // service's placement and Calcite's disagree the sort must decline, and declining is
            // invisible from the rows unless they are compared.
            ("SELECT c.\"id\", c.\"category\" FROM products AS c ORDER BY c.\"category\", c.\"id\"", true),
            ("SELECT c.\"id\", c.\"category\" FROM products AS c ORDER BY c.\"category\" DESC, c.\"id\"", true),
            ("SELECT c.\"id\", c.\"category\" FROM products AS c ORDER BY c.\"category\" NULLS FIRST, c.\"id\"", true),
            ("SELECT c.\"id\", c.\"category\" FROM products AS c ORDER BY c.\"category\" NULLS LAST, c.\"id\"", true),
            ("SELECT c.\"id\" FROM products AS c ORDER BY c.\"_MAP\"['price'], c.\"id\"", true),

            // Row restriction combined with a predicate over a path some documents lack, where a
            // wrongly pushed TOP takes the wrong rows rather than the wrong number of them.
            ("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['price'] IS NOT NULL ORDER BY c.\"id\" FETCH NEXT 2 ROWS ONLY", true),
            ("SELECT c.\"id\" FROM products AS c WHERE NOT (c.\"category\" = 'bikes') ORDER BY c.\"id\" OFFSET 1 ROWS FETCH NEXT 2 ROWS ONLY", true),

            // The aggregate forms over a path that is null in one document and absent in another.
            // What SUM and AVG skip, and what COUNT counts, is the whole question.
            ("SELECT COUNT(*), COUNT(c.\"_MAP\"['price']) FROM products AS c", false),
            ("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY c.\"category\" HAVING COUNT(*) > 1", false),

            // Grouping by something other than the partition key, and by more than one thing.
            ("SELECT c.\"_MAP\"['name'], COUNT(*) FROM products AS c GROUP BY c.\"_MAP\"['name']", false),
            ("SELECT c.\"category\", c.\"_MAP\"['name'], COUNT(*) FROM products AS c GROUP BY c.\"category\", c.\"_MAP\"['name']", false),

            // DISTINCT over a computed column and over more than one, where the normalised key and
            // the plain one sit side by side.
            ("SELECT DISTINCT c.\"category\", c.\"_MAP\"['name'] FROM products AS c", false),
            ("SELECT DISTINCT c.\"_MAP\"['price'] FROM products AS c", false),

            // The whole document, and a document with no user properties beyond the seeded ones.
            ("SELECT c.\"_MAP\" FROM products AS c", false),
            ("SELECT c.\"_MAP\"['metadata'] FROM products AS c", false),

            // Casts over document values, which is how a view gives a column a SQL type over this row
            // model. Read against the typed container, whose documents disagree with the declaration on
            // purpose: a string where it says integer, a fraction where it says integer, the property
            // absent, and the property null.
            //
            // These agree because nothing here is pushed. A cast is opaque to translation, so every
            // operator reading one declines and Calcite answers it over the whole container — slowly,
            // and with the rows SQL says. That is the property under test: it is what makes looking
            // through a cast a change that has to prove itself here first. Measured, an erasing
            // translation fails these — Calcite converts "30" and 30.7 to 30 and matches both, and the
            // service compares the stored value as it stands and matches neither.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) = 30", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) > 10", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) > 0 ORDER BY c.\"id\"", true),
            // Saturation. A stored value far past what the target can hold converts to the limit, so a
            // comparison against the limit is true of it — and a bound around the limit would exclude
            // exactly that document. Measured as a lost row before the bound stopped stating that side.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['big'] AS INTEGER) = 2147483647", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['big'] AS BIGINT) = 9223372036854775807", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['big'] AS INTEGER) > 5", false),

            // The spellings differ in what they do with a value that will not convert -- CAST raises,
            // SAFE_CAST yields null -- and the bound must not change which happens, because it never
            // excludes a value that is not a number.
            ("SELECT c.\"id\" FROM typed AS c WHERE SAFE_CAST(c.\"_MAP\"['price'] AS INTEGER) = 30", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE SAFE_CAST(c.\"_MAP\"['price'] AS INTEGER) > 10", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE SAFE_CAST(c.\"_MAP\"['big'] AS INTEGER) = 2147483647", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE SAFE_CAST(c.\"_MAP\"['label'] AS VARCHAR) = 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['price'] AS INTEGER) IS NULL", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['price'] AS DOUBLE) = 30.7", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['name'] AS VARCHAR) = 'Stringy'", false),
            ("SELECT c.\"id\", CAST(c.\"_MAP\"['price'] AS INTEGER) FROM typed AS c", false),
            ("SELECT CAST(c.\"_MAP\"['price'] AS INTEGER) FROM typed AS c WHERE c.\"category\" = 'a'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"category\" AS VARCHAR) = 'b'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['price'] AS DECIMAL(10, 2)) = 30", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"id\" AS INTEGER) = 3", false),

            // Equality against text, which is the one cast shape that is dropped — and dropped because
            // the two forms select the same documents, not because the difference is tolerable. Asked
            // of a field holding a string, a number, a boolean, an array, an object, null and nothing.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['label'] AS VARCHAR) = 'bikes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['label'] AS VARCHAR) = 'shoes'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['label'] AS VARCHAR) <> 'bikes'", false),

            // The literals that are refused, each because some other JSON value renders as them. If any
            // of these starts being dropped, these are the statements that say so.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['label'] AS VARCHAR) = '30'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"_MAP\"['label'] AS VARCHAR) = 'true'", false),

            // The partition key reached through a view, which is the routing this recovers. The rows
            // must not change; that they are fetched from one partition is measured separately.
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"category\" AS VARCHAR) = 'b'", false),
            ("SELECT c.\"id\" FROM typed AS c WHERE CAST(c.\"category\" AS VARCHAR) = '30'", false),
            ("SELECT CAST(c.\"_MAP\"['price'] AS INTEGER), COUNT(*) FROM typed AS c GROUP BY CAST(c.\"_MAP\"['price'] AS INTEGER)", false),
        ];

        /// <summary>
        /// The statements the pushdown answers differently, each with what makes it differ.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Empty, and that is the point of keeping it.</b> Repairing the oracle put five statements
        /// here — negation over a null-valued property, grouping and dedup over one, and an
        /// <c>ARRAY_SLICE</c> origin adjustment that was never needed — and each has since been fixed
        /// and promoted into the corpus. What remains is the mechanism.
        /// </para>
        /// <para>
        /// A statement belongs here only while it is an open defect with a witness, never as a
        /// decision. Asserting that each still differs is what keeps that true: one that starts
        /// agreeing fails <see cref="EveryRecordedDivergenceStillDiverges"/> and is meant to move up
        /// into the corpus rather than sit here looking settled.
        /// </para>
        /// </remarks>
        static readonly (string Sql, bool Ordered, string Reason)[] Divergences =
        [
        ];

        /// <summary>
        /// Statements with no oracle to compare against, and why there is none.
        /// </summary>
        static readonly (string Sql, string Reason)[] WithoutAnOracle =
        [
            ("SELECT c.\"id\" FROM products AS c, UNNEST(c.\"_MAP\"['tags']) AS t",
                "Withholding the unnest rule leaves the correlate with no implementation in the asynchronous convention at all, so the unpushed plan cannot be built. Comparing the traversal needs an oracle that reads the array in process, which is a way in rather than a rule taken away."),
        ];

        [TestMethod]
        public async Task EveryStatementAgreesWithTheOracle()
        {
            // Gated here, outside any catch: raising the gate inside Run turned "no emulator" into
            // twenty-seven failures on every platform without one, which is how CI first said so.
            if (_container is null)
                Assert.Inconclusive("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var failures = new List<string>();

            foreach (var (sql, ordered) in Corpus)
                if (await Compare(sql, ordered) is string failure)
                    failures.Add(failure);

            failures.Should().BeEmpty("every pushdown must answer as Calcite would:\n" + string.Join("\n", failures));
        }

        [TestMethod]
        public async Task EveryRecordedDivergenceStillDiverges()
        {
            if (_container is null)
                Assert.Inconclusive("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var agreed = new List<string>();

            foreach (var (sql, ordered, reason) in Divergences)
                if (await Compare(sql, ordered) is null)
                    agreed.Add($"{sql}\n  recorded as: {reason}");

            // Agreement is the good outcome and still a failure here, because the record is now wrong.
            // Move the statement into the corpus and delete its entry.
            agreed.Should().BeEmpty("a recorded divergence that has closed belongs in the corpus:\n" + string.Join("\n", agreed));
        }

        /// <summary>
        /// Runs what cannot be compared, so that it is at least known to answer.
        /// </summary>
        /// <remarks>
        /// Weaker than the corpus by a long way, and the strongest thing available while the oracle
        /// cannot be built for these — see <see cref="WithoutAnOracle"/>. It still catches a pushdown
        /// that stops running at all.
        /// </remarks>
        [TestMethod]
        public async Task EveryStatementWithoutAnOracleStillRuns()
        {
            if (_container is null)
                Assert.Inconclusive("Differential testing needs a service. " + (_initializationFailure ?? "No account is reachable at " + Endpoint));

            var failures = new List<string>();

            foreach (var (sql, reason) in WithoutAnOracle)
            {
                try
                {
                    await Run(sql, pushdown: true);
                }
                catch (Exception e) when (e is not AssertInconclusiveException)
                {
                    failures.Add($"{sql}\n  no oracle because: {reason}\n  and the pushdown failed to run: {e.Message}");
                }
            }

            failures.Should().BeEmpty("a statement with no oracle must at least answer:\n" + string.Join("\n", failures));
        }

    }

}
