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

        static CosmosClient? _client;
        static Container? _container;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                LimitToEndpoint = true,
                RequestTimeout = TimeSpan.FromSeconds(5),
                MaxRetryAttemptsOnRateLimitedRequests = 0,
                ServerCertificateCustomValidationCallback = (_, _, _) => true,
            };

            try
            {
                // Bounded so that a run with no emulator skips promptly rather than stalling every
                // job in the CI matrix.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var client = new CosmosClient(EmulatorEndpoint, EmulatorKey, options);

                var database = (await client.CreateDatabaseIfNotExistsAsync("calcite_cosmos_tests", cancellationToken: cts.Token)).Database;

                var properties = new ContainerProperties("products", "/category");
                properties.IndexingPolicy.CompositeIndexes.Add(new System.Collections.ObjectModel.Collection<CompositePath>
                {
                    new CompositePath { Path = "/name", Order = CompositePathSortOrder.Ascending },
                    new CompositePath { Path = "/price", Order = CompositePathSortOrder.Ascending },
                });

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
                Assert.Inconclusive("No Cosmos DB emulator reachable at " + EmulatorEndpoint);

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
                ("unnest ordered by element", b => { b.AddUnnest("t0", "c.tags"); b.SelectValue("t0"); b.AddOrderBy("t0", false); }),
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
                            using var reader = new StreamReader(response.Content);
                            failures.Add($"{label}: [{sql}] -> {(await reader.ReadToEndAsync()).Replace("\r", " ").Replace("\n", " ")}");
                            break;
                        }
                    }
                }
                catch (CosmosException e)
                {
                    failures.Add($"{label}: [{sql}] -> {e.Message.Split('\n')[0]}");
                }
            }

            failures.Should().BeEmpty();
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
