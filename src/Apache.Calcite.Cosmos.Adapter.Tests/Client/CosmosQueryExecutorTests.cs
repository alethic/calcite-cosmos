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

    }

}
