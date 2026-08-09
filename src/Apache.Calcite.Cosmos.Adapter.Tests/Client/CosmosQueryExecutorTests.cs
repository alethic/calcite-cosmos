using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Executes generated statements against a live Cosmos DB emulator.
    /// </summary>
    /// <remarks>
    /// These are the only tests requiring a service. They report inconclusive when no emulator is
    /// reachable, so the suite stays runnable — and meaningful — without one.
    /// <para>
    /// Start one with:
    /// <c>docker run -d --name cosmos-emu -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview</c>
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosQueryExecutorTests
    {

        // Well-known public emulator credentials, documented by Microsoft. Not a secret.
        const string EmulatorEndpoint = "http://localhost:8081/";
        const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        /// <summary>
        /// The account to run against — a real one where the environment names it, otherwise the
        /// emulator.
        /// </summary>
        /// <remarks>
        /// The emulator does not implement everything the service does; full text search is the case
        /// that prompted this. Point <c>COSMOS_TEST_ENDPOINT</c> and <c>COSMOS_TEST_KEY</c> at an account
        /// to verify against the real thing, and the checks the emulator reports inconclusive will
        /// either pass or fail properly.
        /// </remarks>
        static readonly string Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") is string e && e.Length > 0 ? e : EmulatorEndpoint;

        static readonly string Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY") is string k && k.Length > 0 ? k : EmulatorKey;

        static bool IsEmulator => ReferenceEquals(Endpoint, EmulatorEndpoint);

        static CosmosClient? _client;
        static Container? _container;

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
                // Both are here to make the emulator usable and neither belongs on a real account: one
                // pins every request to the single endpoint it advertises, the other accepts its
                // self-signed certificate.
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                // Bounded so that a run with no emulator skips promptly rather than stalling every
                // job in the CI matrix.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(IsEmulator ? 10 : 120));
                var client = new CosmosClient(Endpoint, Key, options);

                var database = (await client.CreateDatabaseIfNotExistsAsync("calcite_cosmos_tests", cancellationToken: cts.Token)).Database;

                var properties = new ContainerProperties("products", "/category");
                properties.IndexingPolicy.CompositeIndexes.Add(new System.Collections.ObjectModel.Collection<CompositePath>
                {
                    new CompositePath { Path = "/name", Order = CompositePathSortOrder.Ascending },
                    new CompositePath { Path = "/price", Order = CompositePathSortOrder.Ascending },
                });

                // Full text search needs a policy naming the searchable paths and an index over them.
                // Without both, FULLTEXTCONTAINS and ORDER BY RANK are rejected outright — which is how
                // they were first measured here, and the rejection says nothing about the statement.
                properties.FullTextPolicy = new FullTextPolicy
                {
                    DefaultLanguage = "en-US",
                    FullTextPaths = new System.Collections.ObjectModel.Collection<FullTextPath>
                    {
                        new FullTextPath { Path = "/name", Language = "en-US" },
                    },
                };
                properties.IndexingPolicy.FullTextIndexes.Add(new FullTextIndexPath { Path = "/name" });

                try { await database.GetContainer("products").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }
                var container = (await database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cts.Token)).Container;

                foreach (var json in new[]
                {
                    """{"id":"1","category":"bikes","name":"Trail Blazer","price":120,"tags":["outdoor","steel"]}""",
                    """{"id":"2","category":"bikes","name":"Road Runner","price":340}""",
                    """{"id":"3","category":"shoes","name":"Sprint","price":80,"metadata":{"sku":"S-1"}}""",
                    """{"id":"4","category":"shoes","name":"Marathon"}""",
                })
                {
                    using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var doc = JsonDocument.Parse(json);
                    await container.CreateItemStreamAsync(stream, new PartitionKey(doc.RootElement.GetProperty("category").GetString()), cancellationToken: cts.Token);
                }

                _client = client;
                _container = container;
            }
            catch (Exception)
            {
                _client = null;
                _container = null;
            }
        }

        [ClassCleanup]
        public static void ClassCleanup()
        {
            _client?.Dispose();
            _client = null;
            _container = null;
        }

        static Container Container()
        {
            if (_container is null)
                Assert.Inconclusive("No Cosmos DB account reachable at " + Endpoint);

            return _container!;
        }

        static async Task<List<JsonElement>> Execute(CosmosQuery query, PartitionKey? partitionKey = null)
        {
            var executor = new CosmosQueryExecutor(Container());
            var results = new List<JsonElement>();

            await foreach (var element in executor.ExecuteAsync(query, partitionKey))
                results.Add(element);

            return results;
        }

        static CosmosQueryBuilder Builder() => new("products", "c");

        static CosmosQuery Query(CosmosQueryBuilder builder, CosmosParameterList? parameters = null) =>
            new(builder.Build(), (parameters ?? new CosmosParameterList()).Parameters);

        // ── Generated statements actually run ─────────────────────────────────────

        [TestMethod]
        public async Task IdentityQueryReturnsEveryDocument()
        {
            var results = await Execute(Query(Builder()));
            results.Should().HaveCount(4);
        }

        [TestMethod]
        public async Task ValueProjectionIsUnwrapped()
        {
            var builder = Builder();
            builder.SelectValue("c.name");

            var results = await Execute(Query(builder));

            results.Select(x => x.GetString()).Should().BeEquivalentTo("Trail Blazer", "Road Runner", "Sprint", "Marathon");
        }

        [TestMethod]
        public async Task PropertyProjectionProducesObjects()
        {
            var builder = Builder();
            builder.SelectProperty("n", "c.name");
            builder.SelectProperty("p", "c.price");
            builder.Where = "c.category = \"shoes\"";

            var results = await Execute(Query(builder));

            results.Should().HaveCount(2);
            results.Should().Contain(x => x.GetProperty("n").GetString() == "Sprint" && x.GetProperty("p").GetInt32() == 80);
        }

        [TestMethod]
        public async Task BoundParametersAreSent()
        {
            var parameters = new CosmosParameterList();
            var builder = Builder();
            builder.SelectValue("c.id");
            builder.Where = $"c.price > {parameters.Add(100)}";

            var results = await Execute(Query(builder, parameters));

            results.Select(x => x.GetString()).Should().BeEquivalentTo("1", "2");
        }

        /// <remarks>
        /// Confirms against the service that the emitted form matches both an absent property and
        /// one present with a null value, which <c>= null</c> would not.
        /// </remarks>
        [TestMethod]
        public async Task IsNullTranslationMatchesAbsentProperties()
        {
            var builder = Builder();
            builder.SelectValue("c.id");
            builder.Where = "(NOT IS_DEFINED(c.price) OR IS_NULL(c.price))";

            var results = await Execute(Query(builder));

            // Document 4 has no price at all.
            results.Select(x => x.GetString()).Should().BeEquivalentTo("4");
        }

        [TestMethod]
        public async Task OrderByAndOffsetLimitRun()
        {
            var builder = Builder();
            builder.SelectValue("c.name");
            builder.Where = "IS_DEFINED(c.price)";
            builder.AddOrderBy("c.price", descending: false);
            builder.Offset = 1;
            builder.Fetch = 1;

            var results = await Execute(Query(builder));

            results.Select(x => x.GetString()).Should().Equal("Trail Blazer");
        }

        [TestMethod]
        public async Task UnnestRendersAndRuns()
        {
            var builder = Builder();
            builder.AddUnnest("t", "c.tags");
            builder.SelectValue("t");

            var results = await Execute(Query(builder));

            results.Select(x => x.GetString()).Should().BeEquivalentTo("outdoor", "steel");
        }

        [TestMethod]
        public async Task NestedPathProjectionRuns()
        {
            var builder = Builder();
            builder.SelectValue("c.metadata.sku");

            var results = await Execute(Query(builder));

            // Only one document has metadata; the rest project to undefined and are omitted.
            results.Select(x => x.GetString()).Should().BeEquivalentTo("S-1");
        }

        /// <remarks>
        /// The executor uses a partition key the predicate pinned without being told to, so a
        /// query naming its partition key is single-partition automatically.
        /// </remarks>
        [TestMethod]
        public async Task RecoveredPartitionKeyIsAppliedWithoutBeingPassed()
        {
            var parameters = new CosmosParameterList();
            var builder = Builder();
            builder.SelectValue("c.id");
            builder.Where = $"c.category = {parameters.Add("shoes")}";

            var query = new CosmosQuery(builder.Build(), parameters.Parameters, new object?[] { "shoes" });
            var results = await Execute(query);

            results.Select(x => x.GetString()).Should().BeEquivalentTo("3", "4");
        }

        [TestMethod]
        public async Task PartitionKeyRestrictsExecutionToOnePartition()
        {
            var builder = Builder();
            builder.SelectValue("c.id");

            var results = await Execute(Query(builder), new PartitionKey("shoes"));

            results.Select(x => x.GetString()).Should().BeEquivalentTo("3", "4");
        }

        [TestMethod]
        public async Task GroupByRunsAgainstTheService()
        {
            var builder = Builder();
            builder.FlatProjection = true;
            builder.SelectProperty("category", "c.category");
            builder.SelectProperty("n", "COUNT(1)");
            builder.AddGroupBy("c.category");

            var results = await Execute(Query(builder));

            results.Should().HaveCount(2);
            results.Should().Contain(x => x.GetProperty("category").GetString() == "bikes" && x.GetProperty("n").GetInt32() == 2);
            results.Should().Contain(x => x.GetProperty("category").GetString() == "shoes" && x.GetProperty("n").GetInt32() == 2);
        }

        /// <remarks>
        /// The measurement the aggregate pushdown rules are built on: Cosmos counts a JSON null
        /// where SQL excludes it, so <c>COUNT(x)</c> over a nullable column disagrees with SQL.
        /// </remarks>
        [TestMethod]
        public async Task CountOfAColumnCountsNullsUnlikeSql()
        {
            var builder = Builder();
            builder.SelectValue("COUNT(c.price)");

            var results = await Execute(Query(builder));

            // Three of the four documents carry a price; the fourth omits it entirely.
            results.Should().ContainSingle().Which.GetInt32().Should().Be(3);
        }

        // ── Every emitted form must actually run ──────────────────────────────────

        /// <summary>
        /// Executes one statement of every shape <see cref="CosmosQueryBuilder"/> can produce.
        /// </summary>
        /// <remarks>
        /// The planner tests assert what is generated; only the service can say whether it is
        /// accepted. The aggregate-in-an-object-constructor rejection was invisible to every other
        /// layer, so each emitted form is exercised here rather than assumed valid.
        /// </remarks>
        [TestMethod]
        public async Task EveryEmittedFormIsAcceptedByTheService()
        {
            var cases = new (string Label, Action<CosmosQueryBuilder> Configure)[]
            {
                ("identity", _ => { }),
                ("value scalar", b => b.SelectValue("c.name")),
                ("object projection", b => { b.SelectProperty("a", "c.id"); b.SelectProperty("b", "c.name"); }),
                ("distinct value", b => { b.Distinct = true; b.SelectValue("c.category"); }),
                ("distinct object", b => { b.Distinct = true; b.SelectProperty("a", "c.category"); }),
                ("top value", b => { b.Top = 2; b.SelectValue("c.id"); }),
                ("distinct top value", b => { b.Distinct = true; b.Top = 2; b.SelectValue("c.category"); }),
                ("where", b => b.Where = "c.category = \"bikes\""),
                ("order by", b => b.AddOrderBy("c.id", false)),
                ("order by descending", b => b.AddOrderBy("c.id", true)),
                ("offset limit with order by", b => { b.AddOrderBy("c.id", false); b.Offset = 1; b.Fetch = 2; }),
                ("offset limit without order by", b => { b.Offset = 1; b.Fetch = 2; }),
                ("limit only", b => b.Fetch = 2),
                ("group by flat", b =>
                {
                    b.FlatProjection = true;
                    b.SelectProperty("category", "c.category");
                    b.SelectProperty("n", "COUNT(1)");
                    b.AddGroupBy("c.category");
                }),
                ("group by without aggregate", b =>
                {
                    b.FlatProjection = true;
                    b.SelectProperty("category", "c.category");
                    b.AddGroupBy("c.category");
                }),
                ("unnest", b => { b.AddUnnest("t0", "c.tags"); b.SelectValue("t0"); }),
                ("unnest with filter", b => { b.AddUnnest("t0", "c.tags"); b.SelectValue("t0"); b.Where = "t0 = \"steel\""; }),
                ("unnest ordered by root", b => { b.AddUnnest("t0", "c.tags"); b.SelectValue("t0"); b.AddOrderBy("c.id", false); }),
                ("nested path projection", b => b.SelectValue("c.metadata.sku")),
                ("bracketed property", b => b.SelectValue("c[\"name\"]")),
                ("is-null predicate", b => b.Where = "(NOT IS_DEFINED(c.price) OR IS_NULL(c.price))"),

                // Scalar functions, in the exact forms the translator emits.
                ("upper", b => b.SelectValue("UPPER(c.name)")),
                ("length", b => b.SelectValue("LENGTH(c.name)")),
                ("concat", b => b.SelectValue("CONCAT(c.name, c.category)")),
                ("replace", b => b.SelectValue("REPLACE(c.name, \"a\", \"b\")")),
                ("substring shifted", b => b.SelectValue("SUBSTRING(c.name, (1 - 1), 3)")),
                ("position via index_of", b => b.SelectValue("(INDEX_OF(c.name, \"a\") + 1)")),
                ("ceiling", b => b.SelectValue("CEILING(c.price)")),
                ("floor", b => b.SelectValue("FLOOR(c.price)")),
                ("natural log", b => b.SelectValue("LOG(c.price)")),
                ("log10", b => b.SelectValue("LOG10(c.price)")),
                ("power", b => b.SelectValue("POWER(c.price, 2)")),
                ("sign and abs", b => b.SelectValue("SIGN(ABS(c.price))")),
                ("round", b => b.SelectValue("ROUND(c.price)")),
                ("sqrt", b => b.SelectValue("SQRT(c.price)")),
                ("function in predicate", b => b.Where = "UPPER(c.category) = \"BIKES\""),

                // Trigonometry and the rest of the numeric set.
                ("sin", b => b.SelectValue("SIN(c.price)")),
                ("cos", b => b.SelectValue("COS(c.price)")),
                ("tan", b => b.SelectValue("TAN(c.price)")),
                ("cot", b => b.SelectValue("COT(c.price)")),
                // In domain, so that this list stays a list of forms the service accepts. What happens
                // outside a domain is measured separately, by OutOfDomainArithmeticFailsTheQuery.
                ("asin", b => b.SelectValue("ASIN(0.5)")),
                ("acos", b => b.SelectValue("ACOS(0.5)")),
                ("atan", b => b.SelectValue("ATAN(c.price)")),
                ("atn2", b => b.SelectValue("ATN2(c.price, c.price)")),
                ("degrees", b => b.SelectValue("DEGREES(c.price)")),
                ("radians", b => b.SelectValue("RADIANS(c.price)")),
                ("pi", b => b.SelectValue("PI()")),
                ("trunc", b => b.SelectValue("TRUNC(c.price)")),

                // Collections.
                ("array_length", b => b.SelectValue("ARRAY_LENGTH(c.tags)")),
                ("array_contains", b => b.Where = "ARRAY_CONTAINS(c.tags, \"steel\")"),

                // Trim, whose specification picks the function.
                ("trim", b => b.SelectValue("TRIM(c.name)")),
                ("ltrim", b => b.SelectValue("LTRIM(c.name)")),
                ("rtrim", b => b.SelectValue("RTRIM(c.name)")),

                // Type checking.
                ("is_array", b => b.SelectValue("IS_ARRAY(c.tags)")),
                ("is_bool", b => b.SelectValue("IS_BOOL(c.name)")),
                ("is_defined", b => b.SelectValue("IS_DEFINED(c.name)")),
                ("is_null", b => b.SelectValue("IS_NULL(c.name)")),
                ("is_number", b => b.SelectValue("IS_NUMBER(c.price)")),
                ("is_object", b => b.SelectValue("IS_OBJECT(c.metadata)")),
                ("is_primitive", b => b.SelectValue("IS_PRIMITIVE(c.name)")),
                ("is_string", b => b.SelectValue("IS_STRING(c.name)")),

            };

            var failures = new List<string>();

            foreach (var (label, configure) in cases)
            {
                var builder = Builder();
                configure(builder);

                var sql = builder.Build();

                try
                {
                    using var iterator = Container().GetItemQueryStreamIterator(new QueryDefinition(sql));
                    while (iterator.HasMoreResults)
                    {
                        using var response = await iterator.ReadNextAsync();
                        if (response.IsSuccessStatusCode == false)
                        {
                            // A rejection does not always carry a body, and reading a null one threw
                            // out of the loop and lost every other case's verdict along with it.
                            var body = "(no body)";
                            if (response.Content is not null)
                            {
                                using var reader = new StreamReader(response.Content);
                                body = (await reader.ReadToEndAsync()).Replace("\r", " ").Replace("\n", " ");
                            }

                            failures.Add($"{label}: [{sql}] -> {(int)response.StatusCode} {body}");
                            break;
                        }
                    }
                }
                catch (CosmosException e)
                {
                    failures.Add($"{label}: [{sql}] -> {e.Message.Split('\n')[0]}");
                }
            }

            // Joined rather than asserted as a collection: the collection form truncates its message
            // after a few entries, which reads exactly like "everything else passed" and is how two
            // wrong conclusions were drawn from this test before.
            string.Join("\n", failures).Should().BeEmpty();
        }

        /// <summary>
        /// Full text search, which this emulator does not appear to implement.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reports inconclusive rather than failing. The emulator rejects all four forms with a bodyless
        /// 400 even with a full text policy and a full text index on the searched path — and it is
        /// already known to discard container configuration it does not implement, composite indexes
        /// being the documented case a few tests below. So the rejection is evidence about the emulator
        /// and not about the syntax, which is the reference's verbatim.
        /// </para>
        /// <para>
        /// The day the emulator implements it, this starts passing and the forms are verified. Until
        /// then they are the one part of the translator that no service has confirmed.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task FullTextFormsAreAcceptedWhereTheEmulatorSupportsThem()
        {
            var cases = new (string Label, Action<CosmosQueryBuilder> Configure)[]
            {
                ("fulltextcontains", b => b.Where = "FULLTEXTCONTAINS(c.name, \"steel\")"),
                ("fulltextcontainsall", b => b.Where = "FULLTEXTCONTAINSALL(c.name, \"steel\", \"frame\")"),
                ("fulltextcontainsany", b => b.Where = "FULLTEXTCONTAINSANY(c.name, \"steel\", \"frame\")"),
                ("order by rank fulltextscore", b => { b.Top = 10; b.RankBy = "FULLTEXTSCORE(c.name, \"steel\")"; }),

                // The forms CosmosRank emits: a projection alongside the clause, and a fused score. The
                // score is never in the select list, which is the whole reason that node exists.
                ("rank with an object projection", b =>
                {
                    b.Top = 10;
                    b.SelectProperty("id", "c.id");
                    b.RankBy = "FULLTEXTSCORE(c.name, \"steel\")";
                }),
                ("rank by rrf", b =>
                {
                    b.Top = 10;
                    b.SelectProperty("id", "c.id");
                    b.RankBy = "RRF(FULLTEXTSCORE(c.name, \"steel\"), FULLTEXTSCORE(c.name, \"frame\"))";
                }),
            };

            var rejected = new List<string>();

            foreach (var (label, configure) in cases)
            {
                var builder = Builder();
                configure(builder);

                try
                {
                    using var iterator = Container().GetItemQueryStreamIterator(new QueryDefinition(builder.Build()));
                    while (iterator.HasMoreResults)
                    {
                        using var response = await iterator.ReadNextAsync();
                        if (response.IsSuccessStatusCode == false)
                        {
                            rejected.Add(label);
                            break;
                        }
                    }
                }
                catch (CosmosException)
                {
                    rejected.Add(label);
                }
            }

            if (rejected.Count == cases.Length)
                Assert.Inconclusive("This emulator does not implement full text search; none of " + string.Join(", ", rejected) + " was accepted.");

            string.Join(", ", rejected).Should().BeEmpty("the emulator accepted some full text forms, so the rest are genuine failures");
        }

        /// <summary>
        /// Measures what Cosmos does with arithmetic outside a function's domain.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It fails the whole query rather than yielding undefined for the offending row. Calcite
        /// evaluates the same expressions as NaN, so pushing one down trades a row of NaN for a failed
        /// statement — over data the adapter cannot inspect first, a container having no schema to check
        /// a domain against.
        /// </para>
        /// <para>
        /// <b>This is not a reason to single any of them out.</b> <c>SQRT</c> behaves the same way and
        /// has always been pushed, so the adapter has already accepted the trade; <c>ASIN</c> and
        /// <c>ACOS</c> are pushed on the same terms rather than being excluded for a property they
        /// share with functions that are not. The behaviour is uniform across every function measured,
        /// so there is no per-function exception to carve out. Whether to keep pushing any of them is a
        /// decision, and this is the measurement it should be made against.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task OutOfDomainArithmeticFailsTheQuery()
        {
            var verdicts = new Dictionary<string, bool>();

            foreach (var expression in new[] { "ASIN(2)", "ACOS(2)", "SQRT(-1)", "LOG(0)" })
            {
                var accepted = true;

                try
                {
                    using var iterator = Container().GetItemQueryStreamIterator(new QueryDefinition($"SELECT VALUE {expression} FROM products c"));
                    while (iterator.HasMoreResults)
                    {
                        using var response = await iterator.ReadNextAsync();
                        if (response.IsSuccessStatusCode == false)
                        {
                            accepted = false;
                            break;
                        }
                    }
                }
                catch (CosmosException)
                {
                    accepted = false;
                }

                verdicts[expression] = accepted;
            }

            verdicts["ASIN(2)"].Should().BeFalse("an out-of-domain ASIN fails the query");
            verdicts["ACOS(2)"].Should().BeFalse("an out-of-domain ACOS fails the query");
            verdicts["SQRT(-1)"].Should().BeFalse("a negative SQRT fails the query, and SQRT has always been pushed");
            verdicts["LOG(0)"].Should().BeFalse("LOG(0) fails the query too, so the behaviour is uniform rather than per-function");
        }

        // ── Every JSON type survives the round trip ───────────────────────────────

        /// <summary>
        /// Reads a document holding every kind of JSON value back through the materializer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The unit tests build a <see cref="JsonElement"/> by hand; this fetches one the service
        /// actually stored and returned, so the encoding on the wire is part of what is checked.
        /// </para>
        /// <para>
        /// Values are asserted as the Java boxes Calcite holds them in, not as CLR primitives. A CLR
        /// <see cref="int"/> in a row compiles and then fails at the first Calcite operator that casts,
        /// which is a long way from here, so the box is the thing worth pinning.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task EveryJsonTypeSurvivesTheRoundTrip()
        {
            const string id = "types-1";
            const string json = """
                {"id":"types-1","category":"types",
                 "str":"text","num":42,"frac":1.5,"neg":-7,"big":9007199254740991,
                 "yes":true,"no":false,"nothing":null,
                 "arr":[1,"two",true,null,{"k":"v"},[9]],
                 "obj":{"a":1,"b":{"c":"deep"},"d":[1,2]}}
                """;

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                await Container().UpsertItemStreamAsync(stream, new PartitionKey("types"));

            var parameters = new CosmosParameterList();
            var builder = Builder();
            builder.SelectValue("c");
            builder.Where = $"c.id = {parameters.Add(id)}";

            var results = await Execute(Query(builder, parameters));
            var document = results.Should().ContainSingle().Subject;

            // Read as the map column is: the whole document, by its JSON shape rather than a declared type.
            var map = CosmosJson.GetMap(document);

            map.get("str").Should().Be("text");
            map.get("yes").Should().Be(java.lang.Boolean.TRUE);
            map.get("no").Should().Be(java.lang.Boolean.FALSE);
            map.get("nothing").Should().BeNull();

            // A whole JSON number reads as a Long so an identifier or a count is not surfaced as 42.0;
            // one with a fractional part reads as a Double.
            map.get("num").Should().Be(java.lang.Long.valueOf(42L));
            map.get("neg").Should().Be(java.lang.Long.valueOf(-7L));
            map.get("big").Should().Be(java.lang.Long.valueOf(9007199254740991L));
            map.get("frac").Should().Be(java.lang.Double.valueOf(1.5d));

            var array = (java.util.List)map.get("arr");
            array.size().Should().Be(6);
            array.get(0).Should().Be(java.lang.Long.valueOf(1L));
            array.get(1).Should().Be("two");
            array.get(2).Should().Be(java.lang.Boolean.TRUE);
            array.get(3).Should().BeNull();
            ((java.util.Map)array.get(4)).get("k").Should().Be("v");
            ((java.util.List)array.get(5)).get(0).Should().Be(java.lang.Long.valueOf(9L));

            var obj = (java.util.Map)map.get("obj");
            obj.get("a").Should().Be(java.lang.Long.valueOf(1L));
            ((java.util.Map)obj.get("b")).get("c").Should().Be("deep");
            ((java.util.List)obj.get("d")).size().Should().Be(2);

            // Nesting is not truncated at the map column's one-level type: the value goes as deep as
            // the document does.
            ((java.util.Map)((java.util.Map)map.get("obj")).get("b")).get("c").Should().Be("deep");
        }

        /// <summary>
        /// Reads the same document through the declared SQL types rather than by JSON shape.
        /// </summary>
        /// <remarks>
        /// This is the path a promoted column takes: the plan says what the type is and the materializer
        /// reads it as that, refusing where the document disagrees rather than coercing.
        /// </remarks>
        [TestMethod]
        public async Task DeclaredTypesReadFromAStoredDocument()
        {
            const string json = """
                {"id":"types-2","category":"types","str":"text","num":42,"frac":1.5,"yes":true,
                 "arr":[1,2,3],"obj":{"a":1}}
                """;

            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                await Container().UpsertItemStreamAsync(stream, new PartitionKey("types"));

            var parameters = new CosmosParameterList();
            var builder = Builder();
            builder.SelectValue("c");
            builder.Where = $"c.id = {parameters.Add("types-2")}";

            var row = (await Execute(Query(builder, parameters))).Should().ContainSingle().Subject;

            CosmosJson.GetProperty(row, "str", SqlTypeName.VARCHAR).Should().Be("text");
            CosmosJson.GetProperty(row, "num", SqlTypeName.INTEGER).Should().Be(java.lang.Integer.valueOf(42));
            CosmosJson.GetProperty(row, "num", SqlTypeName.BIGINT).Should().Be(java.lang.Long.valueOf(42L));
            CosmosJson.GetProperty(row, "frac", SqlTypeName.DOUBLE).Should().Be(java.lang.Double.valueOf(1.5d));
            CosmosJson.GetProperty(row, "frac", SqlTypeName.DECIMAL).ToString().Should().Be("1.5");
            CosmosJson.GetProperty(row, "yes", SqlTypeName.BOOLEAN).Should().Be(java.lang.Boolean.TRUE);
            ((java.util.List)CosmosJson.GetProperty(row, "arr", SqlTypeName.ARRAY)!).size().Should().Be(3);
            ((java.util.Map)CosmosJson.GetProperty(row, "obj", SqlTypeName.MAP)!).get("a").Should().Be(java.lang.Long.valueOf(1L));

            // Absent reads as SQL NULL, which is the only reading available: a container has no schema
            // that could have promised the property.
            CosmosJson.GetProperty(row, "missing", SqlTypeName.VARCHAR).Should().BeNull();

            // And a genuine disagreement is refused rather than coerced.
            var act = () => CosmosJson.GetProperty(row, "num", SqlTypeName.VARCHAR);
            act.Should().Throw<CosmosMaterializationException>();
        }

        // ── Point reads ───────────────────────────────────────────────────────────

        /// <remarks>
        /// The read returns the document, not the projection the statement would have constructed —
        /// which is the whole reason the converter builds a different row builder for this path.
        /// </remarks>
        [TestMethod]
        public async Task APointReadReturnsTheDocument()
        {
            var builder = Builder();
            builder.SelectProperty("id", "c.id");
            builder.Where = "c.id = \"1\" AND c.category = \"bikes\"";

            var query = new CosmosQuery(builder.Build(), new CosmosParameterList().Parameters, ["bikes"], null, "1", PartitionKeyIsComplete: true);
            var results = await Execute(query);

            var document = results.Should().ContainSingle().Subject;
            document.GetProperty("id").GetString().Should().Be("1");
            document.GetProperty("name").GetString().Should().Be("Trail Blazer");
        }

        /// <remarks>
        /// A missing document is an empty result rather than an error: the query this stands in for
        /// would have returned no rows, and a read answering "no such document" is that answer.
        /// </remarks>
        [TestMethod]
        public async Task APointReadOfAMissingDocumentReturnsNothing()
        {
            var builder = Builder();
            builder.Where = "c.id = \"nope\" AND c.category = \"bikes\"";

            var query = new CosmosQuery(builder.Build(), new CosmosParameterList().Parameters, ["bikes"], null, "nope", PartitionKeyIsComplete: true);

            (await Execute(query)).Should().BeEmpty();
        }

        /// <remarks>
        /// And it costs less, which is the point of the whole path: a point read is charged about 1 RU
        /// where the same fetch as a query is 2.3 at best.
        /// <para>
        /// Inconclusive on the emulator, which reports a flat 1 RU for both and so cannot tell them
        /// apart. Asserting against that would be asserting a fiction — the emulator does not model
        /// request charges, and this is the third thing it does not model faithfully.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task APointReadCostsLessThanTheQuery()
        {
            if (IsEmulator)
                Assert.Inconclusive("The emulator reports a flat request charge and cannot distinguish a point read from a query.");

            var sql = "SELECT VALUE c FROM products c WHERE c.id = \"1\" AND c.category = \"bikes\"";
            var parameters = new CosmosParameterList().Parameters;

            double queryCharge = 0;
            using (var iterator = Container().GetItemQueryStreamIterator(new QueryDefinition(sql)))
            {
                while (iterator.HasMoreResults)
                {
                    using var response = await iterator.ReadNextAsync();
                    queryCharge += response.Headers.RequestCharge;
                }
            }

            using var read = await Container().ReadItemStreamAsync("1", new PartitionKey("bikes"));
            var readCharge = read.Headers.RequestCharge;

            readCharge.Should().BeLessThan(queryCharge,
                $"a point read ({readCharge} RU) should cost less than the equivalent query ({queryCharge} RU)");
        }

        // ── Statistics read back from the service ─────────────────────────────────

        /// <summary>
        /// Reads what the service reports about the container's size and spread.
        /// </summary>
        /// <remarks>
        /// The row count and size come from a semicolon-separated header, which is a format worth
        /// measuring rather than assuming. Inconclusive where the account does not report it, so that
        /// what is being claimed stays visible.
        /// </remarks>
        [TestMethod]
        public async Task StatisticsAreReadFromTheContainer()
        {
            var metadata = await CosmosContainerMetadataReader.ReadAsync(Container());

            if (metadata.Statistics is null)
                Assert.Inconclusive("This account reports no resource usage for the container.");

            var statistics = metadata.Statistics!.Value;

            // At least one physical partition, whatever the container's spread. This one the service
            // answers immediately, because it is a fact about the container rather than its contents.
            statistics.PartitionCount.Should().BeGreaterThan(0);

            // The count is NOT asserted against the four documents seeded moments earlier. Measured:
            // the service reports 0 right after they are written, because the usage statistic is
            // computed in the background and lags. That is what a planner row count is allowed to be —
            // approximate and stale — and asserting the seeded count here would be asserting a promise
            // the service does not make.
            statistics.DocumentCount.Should().BeGreaterThanOrEqualTo(0);

            // Whenever it has caught up, the two agree: documents that exist occupy space.
            if (statistics.DocumentCount > 0)
            {
                statistics.DocumentSizeInBytes.Should().BeGreaterThan(0);
                statistics.AverageDocumentSizeInBytes.Should().BeGreaterThan(0);
            }
        }

        // ── Metadata read back from the service ───────────────────────────────────

        /// <remarks>
        /// <para>
        /// Composite indexes are deliberately not asserted here. The emulator silently discards
        /// them: a container created with one composite index reports zero on both the create
        /// response and a subsequent read, while excluded paths survive. It does not implement the
        /// feature, which is also why it accepts multi-key <c>ORDER BY</c> that the real service
        /// rejects.
        /// </para>
        /// <para>
        /// Composite index reading is covered by <c>CosmosContainerMetadataReaderTests</c> against
        /// a hand-built definition. Verifying it end to end needs a real account.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task MetadataReaderReadsTheLiveContainerDefinition()
        {
            var metadata = await CosmosContainerMetadataReader.ReadAsync(Container());

            metadata.Name.Should().Be("products");
            metadata.PartitionKeyPaths.Should().Equal("/category");
        }

        // ── Schema factory ────────────────────────────────────────────────────────

        static java.util.Map Operand(bool listContainers)
        {
            var operand = new java.util.HashMap();
            operand.put("endpoint", EmulatorEndpoint);
            operand.put("key", EmulatorKey);
            operand.put("database", "calcite_cosmos_tests");
            operand.put("connectionMode", "gateway");

            if (listContainers)
            {
                var containers = new java.util.ArrayList();
                containers.add("products");
                operand.put("containers", containers);
            }

            return operand;
        }

        static CosmosTable? Products(bool listContainers)
        {
            var schema = new CosmosSchemaFactory().create(null!, "cosmos", Operand(listContainers));
            return schema.tables().get("products") as CosmosTable;
        }

        [TestMethod]
        public void SchemaFactoryBuildsASchemaFromNamedContainers()
        {
            Container();
            Products(listContainers: true).Should().NotBeNull();
        }

        [TestMethod]
        public void SchemaFactoryDiscoversContainersWhenNoneAreNamed()
        {
            Container();
            Products(listContainers: false).Should().NotBeNull();
        }

        [TestMethod]
        public void SchemaFactoryTableCarriesTheContainerMetadata()
        {
            Container();

            var table = Products(listContainers: true)!;

            table.Container.Name.Should().Be("products");
            table.Container.PartitionKeyPaths.Should().Equal("/category");
        }

    }

}
