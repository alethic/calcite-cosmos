using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Writes documents to a live Cosmos DB emulator, through the same runtime the plan calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CosmosSequences.WriteAsync"/> is the seam a compiled plan actually enters, so these
    /// drive it rather than the SDK wrapper beneath it: the document a row describes, the partition key
    /// recovered from that document, and the request are one path and are worth testing as one. What
    /// the row means in isolation is covered by <see cref="CosmosDocumentTests"/>, with no service.
    /// </para>
    /// <para>
    /// Its own container, because these add and remove documents and the read fixture asserts a
    /// specific four. Reports inconclusive where no account is reachable, as the read tests do.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosWriteExecutionTests
    {

        // Well-known public emulator credentials, documented by Microsoft. Not a secret.
        const string EmulatorEndpoint = "http://localhost:8081/";
        const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        static readonly string Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") is string e && e.Length > 0 ? e : EmulatorEndpoint;

        static readonly string Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY") is string k && k.Length > 0 ? k : EmulatorKey;

        static bool IsEmulator => ReferenceEquals(Endpoint, EmulatorEndpoint);

        /// <summary>
        /// The database these tests build, named per target framework and distinct from the read tests'.
        /// </summary>
        /// <remarks>
        /// Per framework for the reason the read fixture records — a plain <c>dotnet test</c> runs every
        /// target at once — and distinct from that fixture's so the two cannot drop each other's
        /// containers while both are running.
        /// </remarks>
        static readonly string DatabaseName = "calcite_cosmos_write_tests_" +
            System.Text.RegularExpressions.Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        static readonly string[] Columns = ["_MAP", "id", "_ts", "_etag", "category"];

        static readonly string[] PartitionKeyPaths = ["/category"];

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
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(IsEmulator ? 10 : 120));
                var client = new CosmosClient(Endpoint, Key, options);

                var database = (await client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;

                try { await database.GetContainer("writes").DeleteContainerAsync(cancellationToken: cts.Token); } catch (CosmosException) { }

                _container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("writes", "/category"), cancellationToken: cts.Token)).Container;
                _client = client;
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
            try { _client?.GetDatabase(DatabaseName).DeleteAsync().GetAwaiter().GetResult(); } catch (CosmosException) { }

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

        static java.util.Map Map(params object?[] pairs)
        {
            var map = new java.util.LinkedHashMap();

            for (var i = 0; i + 1 < pairs.Length; i += 2)
                map.put(pairs[i], pairs[i + 1]);

            return map;
        }

        static async IAsyncEnumerable<object?[]> Rows(params object?[][] rows)
        {
            foreach (var row in rows)
            {
                await Task.Yield();
                yield return row;
            }
        }

        /// <summary>
        /// Runs the write the way a compiled plan does.
        /// </summary>
        static async Task<long> Write(CosmosWriteOperation operation, params object?[][] rows)
        {
            var writer = new CosmosQueryExecutor(Container());
            var write = new CosmosWrite(operation, Columns, PartitionKeyPaths);

            await foreach (var count in CosmosSequences.WriteAsync<object?[], long>(Rows(rows), writer, write, r => r!, c => c))
                return count;

            throw new InvalidOperationException("The write yielded no row count.");
        }

        static async Task<JsonElement?> Read(string id, PartitionKey partitionKey)
        {
            using var response = await Container().ReadItemStreamAsync(id, partitionKey);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(response.Content);
            return document.RootElement.Clone();
        }

        [TestMethod]
        public async Task ADocumentDescribedByAMapIsWritten()
        {
            var count = await Write(CosmosWriteOperation.Insert,
                [Map("id", "m1", "category", "bikes", "name", "Trail Blazer", "price", java.lang.Long.valueOf(120)), null, null, null, null]);

            count.Should().Be(1);

            var written = await Read("m1", new PartitionKey("bikes"));

            written.Should().NotBeNull();
            written!.Value.GetProperty("name").GetString().Should().Be("Trail Blazer");
            written!.Value.GetProperty("price").GetInt64().Should().Be(120);
        }

        [TestMethod]
        public async Task ADocumentDescribedByPromotedColumnsIsWritten()
        {
            var count = await Write(CosmosWriteOperation.Insert, [null, "p1", null, null, "shoes"]);

            count.Should().Be(1);

            var written = await Read("p1", new PartitionKey("shoes"));

            written.Should().NotBeNull();
            written!.Value.GetProperty("category").GetString().Should().Be("shoes");
        }

        /// <summary>
        /// A document carrying another's bookkeeping is written, and comes back with its own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The map column of a document read from a container holds <c>_ts</c>, <c>_etag</c>,
        /// <c>_rid</c>, <c>_self</c> and <c>_attachments</c> alongside the caller's own properties, so
        /// this is the shape <c>INSERT … SELECT "_MAP" FROM …</c> produces.
        /// </para>
        /// <para>
        /// <b>It does not test the stripping, and it was written believing it did.</b> Probed by
        /// removing <see cref="CosmosDocument.IsServiceProperty"/>'s answer: this still passed, and only
        /// the unit test failed. The service assigns its own values whatever is supplied, so what this
        /// establishes is that fact — worth keeping, because it is the reason the stripping is a
        /// decision about what the adapter writes rather than something the service requires.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task ServicePropertiesSuppliedByARowAreReplacedByTheService()
        {
            var count = await Write(CosmosWriteOperation.Insert,
                [Map("id", "c1", "category", "bikes", "_ts", java.lang.Long.valueOf(1), "_etag", "\"nonsense\"", "_rid", "bogus", "name", "Copy"), null, null, null, null]);

            count.Should().Be(1);

            var written = await Read("c1", new PartitionKey("bikes"));

            written.Should().NotBeNull();
            written!.Value.GetProperty("name").GetString().Should().Be("Copy");
            written!.Value.GetProperty("_ts").GetInt64().Should().BeGreaterThan(1);
            written!.Value.GetProperty("_etag").GetString().Should().NotBe("\"nonsense\"");
        }

        /// <summary>
        /// A document with no value at the partition key path lives in the "none" logical partition.
        /// </summary>
        /// <remarks>
        /// A real place, and a different one from where a null key routes to. Getting this wrong writes
        /// the document to the wrong partition rather than failing, so it is checked by reading it back
        /// from the partition it should be in.
        /// </remarks>
        [TestMethod]
        public async Task ADocumentWithoutAPartitionKeyGoesToTheNonePartition()
        {
            var count = await Write(CosmosWriteOperation.Insert,
                [Map("id", "n1", "name", "Unfiled"), null, null, null, null]);

            count.Should().Be(1);

            (await Read("n1", PartitionKey.None)).Should().NotBeNull();
        }

        /// <summary>
        /// <c>INSERT</c> creates, so a repeat is a conflict rather than a replacement.
        /// </summary>
        [TestMethod]
        public async Task ADuplicateIdIsRefused()
        {
            await Write(CosmosWriteOperation.Insert, [Map("id", "d1", "category", "bikes", "name", "First"), null, null, null, null]);

            var act = async () => await Write(CosmosWriteOperation.Insert, [Map("id", "d1", "category", "bikes", "name", "Second"), null, null, null, null]);

            await act.Should().ThrowAsync<CosmosExecutionException>().WithMessage("*409*");

            // And the first is untouched, which is the half of "refused" worth stating.
            var written = await Read("d1", new PartitionKey("bikes"));
            written!.Value.GetProperty("name").GetString().Should().Be("First");
        }

        [TestMethod]
        public async Task ADeletedDocumentIsGone()
        {
            await Write(CosmosWriteOperation.Insert, [Map("id", "x1", "category", "shoes"), null, null, null, null]);

            var count = await Write(CosmosWriteOperation.Delete, [Map("id", "x1", "category", "shoes"), null, null, null, null]);

            count.Should().Be(1);
            (await Read("x1", new PartitionKey("shoes"))).Should().BeNull();
        }

        /// <summary>
        /// A row naming a document that is not there affects nothing, and is not an error.
        /// </summary>
        /// <remarks>
        /// The rows were read before they were deleted, so a document gone by the time the delete
        /// arrives was deleted by someone else. Reporting a smaller count is the honest answer.
        /// </remarks>
        [TestMethod]
        public async Task DeletingWhatIsNotThereAffectsNothing()
        {
            var count = await Write(CosmosWriteOperation.Delete, [Map("id", "absent", "category", "shoes"), null, null, null, null]);

            count.Should().Be(0);
        }

        [TestMethod]
        public async Task EveryRowIsWrittenAndCounted()
        {
            var count = await Write(CosmosWriteOperation.Insert,
                [Map("id", "b1", "category", "bikes"), null, null, null, null],
                [Map("id", "b2", "category", "bikes"), null, null, null, null],
                [null, "b3", null, null, "shoes"]);

            count.Should().Be(3);

            (await Read("b1", new PartitionKey("bikes"))).Should().NotBeNull();
            (await Read("b2", new PartitionKey("bikes"))).Should().NotBeNull();
            (await Read("b3", new PartitionKey("shoes"))).Should().NotBeNull();
        }

        /// <summary>
        /// A write is charged, and is reported as a write rather than as a query.
        /// </summary>
        [TestMethod]
        public async Task AWriteIsMeasured()
        {
            var charges = new List<double>();

            using var listener = new System.Diagnostics.Metrics.MeterListener();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == CosmosInstrumentation.Name && instrument.Name == "cosmos.request_charge")
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
            {
                foreach (var tag in tags)
                    if (tag.Key == "cosmos.request_kind" && (string?)tag.Value == CosmosInstrumentation.Kinds.Write)
                        lock (charges)
                            charges.Add(measurement);
            });
            listener.Start();

            await Write(CosmosWriteOperation.Insert, [Map("id", "r1", "category", "bikes"), null, null, null, null]);

            listener.RecordObservableInstruments();

            charges.Should().NotBeEmpty();
            charges.Should().OnlyContain(c => c > 0);
        }

    }

}
