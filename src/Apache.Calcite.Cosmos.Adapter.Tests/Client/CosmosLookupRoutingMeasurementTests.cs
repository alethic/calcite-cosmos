using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Measures how the service prices the lookup join's restriction against the alternatives the
    /// shuffle design would choose between.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lookup join sends one cross-partition <c>k IN (…)</c> query per batch. The shuffle idea
    /// is to route instead — per key with the partition key pinned, or per feed range — and whether
    /// that is worth building turns entirely on how the service prices the three forms. The emulator
    /// cannot answer: it reports a flat request charge and typically holds one physical partition.
    /// </para>
    /// <para>
    /// These tests therefore require a real account (<c>COSMOS_TEST_ENDPOINT</c> /
    /// <c>COSMOS_TEST_KEY</c>) and report inconclusive without one. They provision a container at
    /// 21,000 RU/s to force multiple physical partitions — the fixture verifies more than one feed
    /// range and reports inconclusive otherwise — and drop the database afterwards.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosLookupRoutingMeasurementTests
    {

        static readonly string? Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT");
        static readonly string? Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY");

        static readonly string DatabaseName = "calcite_cosmos_routing_" +
            System.Text.RegularExpressions.Regex.Replace(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, "[^A-Za-z0-9]", "_");

        /// <summary>Distinct partition key values seeded, two documents each.</summary>
        const int KeyCount = 300;

        static CosmosClient? _client;
        static Container? _container;
        static int _feedRangeCount;
        static string? _initializationFailure;

        [ClassInitialize]
        public static async Task ClassInitialize(TestContext context)
        {
            if (string.IsNullOrEmpty(Endpoint) || string.IsNullOrEmpty(Key))
            {
                _initializationFailure = "COSMOS_TEST_ENDPOINT / COSMOS_TEST_KEY are not set.";
                return;
            }

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                var client = new CosmosClient(Endpoint, Key, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });

                var database = (await client.CreateDatabaseIfNotExistsAsync(DatabaseName, cancellationToken: cts.Token)).Database;

                // 21,000 RU/s: a physical partition serves at most 10,000, so the container splits
                // into at least three — which is what makes fan-out visible in the charge.
                var container = (await database.CreateContainerIfNotExistsAsync(
                    new ContainerProperties("routing", "/k"),
                    ThroughputProperties.CreateManualThroughput(21000),
                    cancellationToken: cts.Token)).Container;

                // Each task owns its stream: a using in the loop would dispose it while the
                // request is still in flight.
                async Task Seed(string key, string json)
                {
                    using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(json));
                    using var response = await container.CreateItemStreamAsync(stream, new PartitionKey(key), cancellationToken: cts.Token);

                    // A conflict is a rerun against a surviving database, and the document is there.
                    if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
                        response.EnsureSuccessStatusCode();
                }

                var seed = new List<Task>();
                for (var i = 0; i < KeyCount; i++)
                {
                    var key = K(i);
                    for (var d = 0; d < 2; d++)
                    {
                        seed.Add(Seed(key, $$"""{"id":"{{key}}-{{d}}","k":"{{key}}","v":{{i}}}"""));

                        if (seed.Count == 50)
                        {
                            await Task.WhenAll(seed);
                            seed.Clear();
                        }
                    }
                }

                await Task.WhenAll(seed);

                _feedRangeCount = (await container.GetFeedRangesAsync(cts.Token)).Count;
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

        static string K(int i) => $"k{i:D3}";

        static Container Container()
        {
            if (_container is null)
                Assert.Inconclusive("This measurement needs a real account. " + (_initializationFailure ?? "The fixture did not run."));

            if (_feedRangeCount < 2)
                Assert.Inconclusive($"This measurement needs a container spanning several physical partitions; this one has {_feedRangeCount} feed range(s).");

            return _container!;
        }

        /// <summary>
        /// Runs a statement and totals its charge, pages, and returned documents.
        /// </summary>
        static async Task<(double Charge, int Pages, int Documents, string Diagnostics)> Measure(
            QueryDefinition query, PartitionKey? partitionKey = null, FeedRange? feedRange = null)
        {
            var options = new QueryRequestOptions();
            if (partitionKey is PartitionKey pk)
                options.PartitionKey = pk;

            using var iterator = feedRange is null
                ? Container().GetItemQueryStreamIterator(query, requestOptions: options)
                : Container().GetItemQueryStreamIterator(feedRange, query, continuationToken: null, requestOptions: options);

            double charge = 0;
            var pages = 0;
            var documents = 0;
            var diagnostics = new StringBuilder();

            while (iterator.HasMoreResults)
            {
                using var response = await iterator.ReadNextAsync();
                response.EnsureSuccessStatusCode();

                charge += response.Headers.RequestCharge;
                pages++;
                diagnostics.AppendLine(response.Diagnostics?.ToString());

                using var document = await System.Text.Json.JsonDocument.ParseAsync(response.Content);
                documents += document.RootElement.GetProperty("Documents").GetArrayLength();
            }

            return (charge, pages, documents, diagnostics.ToString());
        }

        static QueryDefinition InQuery(IReadOnlyList<string> keys)
        {
            var parameters = new List<string>();
            for (var i = 0; i < keys.Count; i++)
                parameters.Add("@k" + i);

            var query = new QueryDefinition($"SELECT * FROM c WHERE c.k IN ({string.Join(", ", parameters)})");
            for (var i = 0; i < keys.Count; i++)
                query = query.WithParameter("@k" + i, keys[i]);

            return query;
        }

        /// <summary>
        /// The measurement that closed the shuffle idea: the SDK's query router already does
        /// everything a shuffle would.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Measured on a real account, four physical partitions, ten keys over three hundred:
        /// <c>IN(10)</c> as one query cost 12.26 RU in 4 pages — one page per partition; ten
        /// per-key pinned queries cost 28.50 RU, each paying the ~2.85 RU per-query floor; the same
        /// <c>IN(10)</c> issued once per feed range cost 12.26 RU in 4 pages, identical to the
        /// plain query, because cross-partition execution already <em>is</em> per-feed-range
        /// fan-out; a single-key <c>IN</c> with no partition key pinned cost 2.84 RU in 1 page —
        /// the router prunes an <c>IN</c> over the partition key to the partitions owning the
        /// values; and the padded hundred-parameter form the lookup actually emits priced
        /// identically to the clean ten.
        /// </para>
        /// <para>
        /// What is asserted is the relations rather than the charges, which the service may
        /// reprice: routing prunes, per-key routing pays a floor the batch does not, feed-range
        /// grouping buys nothing, padding costs nothing.
        /// </para>
        /// </remarks>
        [TestMethod]
        public async Task TheLookupRestrictionIsAlreadyRoutedByTheSdk()
        {
            // Ten keys spread across the key space, so they land on every physical partition with
            // high probability — the shape a lookup batch actually has.
            var keys = new List<string>();
            for (var i = 0; i < 10; i++)
                keys.Add(K(i * 29));

            // Form 1: one cross-partition IN, the shape the lookup join sends today.
            var one = await Measure(InQuery(keys));

            // Form 2: one pinned query per key — the per-key shuffle.
            double pinnedCharge = 0;
            var pinnedPages = 0;
            var pinnedDocuments = 0;
            foreach (var key in keys)
            {
                var r = await Measure(InQuery(new[] { key }), new PartitionKey(key));
                pinnedCharge += r.Charge;
                pinnedPages += r.Pages;
                pinnedDocuments += r.Documents;
            }

            // Form 3: the full IN once per feed range — the group-by-feed-range shuffle.
            double rangeCharge = 0;
            var rangePages = 0;
            var rangeDocuments = 0;
            var ranges = await Container().GetFeedRangesAsync();
            foreach (var range in ranges)
            {
                var r = await Measure(InQuery(keys), feedRange: range);
                rangeCharge += r.Charge;
                rangePages += r.Pages;
                rangeDocuments += r.Documents;
            }

            // A single-key IN with and without the partition key pinned, which isolates whether the
            // service already routes an IN over the partition key.
            var single = await Measure(InQuery(new[] { keys[0] }));
            var singlePinned = await Measure(InQuery(new[] { keys[0] }), new PartitionKey(keys[0]));

            // The form the lookup join actually emits: padded to the statement's full parameter
            // count by repeating a key, one hundred parameters over ten distinct values.
            var padded = new List<string>(100);
            padded.AddRange(keys);
            while (padded.Count < 100)
                padded.Add(keys[keys.Count - 1]);

            var paddedRun = await Measure(InQuery(padded));

            // Every form answers the same question with the same rows.
            pinnedDocuments.Should().Be(one.Documents);
            rangeDocuments.Should().Be(one.Documents);
            paddedRun.Documents.Should().Be(one.Documents);

            // The router prunes: a single-key IN over the partition key contacts one partition
            // without any pinning.
            single.Pages.Should().Be(1, "the router restricts an IN over the partition key to the partitions owning its values");
            single.Charge.Should().BeApproximately(singlePinned.Charge, singlePinned.Charge * 0.25);

            // Per-key routing pays a per-query floor the batch does not — the measured reason the
            // lookup join keeps its single batched statement.
            pinnedCharge.Should().BeGreaterThan(one.Charge * 1.5, "each pinned query pays the per-query floor");

            // Issuing the statement once per feed range reproduces what the SDK's cross-partition
            // execution already does, page for page.
            rangePages.Should().Be(one.Pages);
            rangeCharge.Should().BeApproximately(one.Charge, one.Charge * 0.15);

            // Padding to the statement's fixed parameter count is free.
            paddedRun.Charge.Should().BeApproximately(one.Charge, one.Charge * 0.15);
        }

    }

}
