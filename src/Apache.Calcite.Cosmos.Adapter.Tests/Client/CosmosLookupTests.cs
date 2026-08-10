using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Covers the lookup join's runtime: what it fetches, and what it pairs up.
    /// </summary>
    /// <remarks>
    /// No service and no planner. What is being checked is that a batch of build rows becomes the right
    /// statement and the right rows — which is where this feature can be wrong in ways that look like a
    /// correct answer.
    /// </remarks>
    [TestClass]
    public class CosmosLookupTests
    {

        /// <summary>
        /// Answers with documents and records every statement it was given.
        /// </summary>
        sealed class RecordingExecutor : ICosmosQueryExecutor
        {

            readonly Func<CosmosQuery, IEnumerable<string>> _documents;

            public RecordingExecutor(Func<CosmosQuery, IEnumerable<string>> documents)
            {
                _documents = documents;
            }

            public RecordingExecutor(params string[] documents) :
                this(_ => documents)
            {

            }

            public List<CosmosQuery> Executed { get; } = new();

            public async IAsyncEnumerable<JsonElement> ExecuteAsync(CosmosQuery query, PartitionKey? partitionKey = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Executed.Add(query);

                foreach (var document in _documents(query))
                {
                    await Task.Yield();
                    yield return JsonDocument.Parse(document).RootElement.Clone();
                }
            }

        }

        static CosmosQuery Query() => new("SELECT VALUE c FROM products c WHERE c.category IN (@k0, @k1, @k2)", Array.Empty<CosmosParameter>());

        /// <summary>The keys a statement was given, in order.</summary>
        static object?[] Keys(CosmosQuery query) => query.Parameters.Where(p => p.Name.StartsWith("@k")).Select(p => p.Value).ToArray();

        static async IAsyncEnumerable<T> Async<T>(params T[] items)
        {
            foreach (var item in items)
            {
                await Task.Yield();
                yield return item;
            }
        }

        static Task<List<string>> Join(IAsyncEnumerable<string> build, RecordingExecutor executor, int batchSize = 3, CosmosQuery? query = null, int cacheSize = 0, CosmosLookupCache? shared = null)
        {
            return Collect(CosmosLookup.JoinAsync(
                build,
                executor,
                query ?? Query(),
                "@k",
                batchSize,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => b + "/" + p,
                cacheSize,
                shared));
        }

        static async Task<List<string>> Collect(IAsyncEnumerable<string> rows)
        {
            var results = new List<string>();
            await foreach (var row in rows)
                results.Add(row);

            return results;
        }

        // ── What it fetches ───────────────────────────────────────────────────────

        [TestMethod]
        public async Task OnlyTheBatchesKeysAreFetched()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            await Join(Async("bikes"), executor);

            executor.Executed.Should().ContainSingle();
            Keys(executor.Executed[0]).Should().Equal("bikes", "bikes", "bikes");
        }

        /// <remarks>
        /// The reason the keys are carried as data rather than rendered into a predicate. A hundred
        /// build rows over two keys is two keys, and the statement is the same length either way.
        /// </remarks>
        [TestMethod]
        public async Task RepeatedKeysAreFetchedOnce()
        {
            var executor = new RecordingExecutor();

            await Join(Async("bikes", "bikes", "shoes"), executor);

            executor.Executed.Should().ContainSingle();
            Keys(executor.Executed[0]).Distinct().Should().BeEquivalentTo(new[] { "bikes", "shoes" });
        }

        /// <remarks>
        /// The statement carries a fixed number of key parameters because it is rendered once. A short
        /// batch therefore repeats a key it already has, which selects the same documents.
        /// </remarks>
        [TestMethod]
        public async Task AShortBatchPadsWithAKeyItAlreadyCarries()
        {
            var executor = new RecordingExecutor();

            await Join(Async("bikes"), executor);

            var keys = Keys(executor.Executed[0]);
            keys.Should().HaveCount(3);
            keys.Distinct().Should().Equal("bikes");
        }

        [TestMethod]
        public async Task RowsBeyondTheBatchSizeFetchAgain()
        {
            var executor = new RecordingExecutor();

            await Join(Async("a", "b", "c", "d"), executor, batchSize: 3);

            executor.Executed.Should().HaveCount(2);
            Keys(executor.Executed[0]).Should().Equal("a", "b", "c");
            Keys(executor.Executed[1]).Should().Equal("d", "d", "d");
        }

        /// <remarks>
        /// A null key joins to nothing, so it contributes no key — and a batch of nothing but those
        /// asks the service for nothing at all, which is the saving this whole path exists for.
        /// </remarks>
        [TestMethod]
        public async Task ABatchOfOnlyNullKeysFetchesNothing()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await Collect(CosmosLookup.JoinAsync(
                Async<string?>(null, null),
                executor,
                Query(),
                "@k",
                3,
                b => b,
                element => element.GetProperty("category").GetString()!,
                p => p,
                (b, p) => (b ?? "null") + "/" + p));

            executor.Executed.Should().BeEmpty();
            rows.Should().BeEmpty();
        }

        // ── What it pairs up ──────────────────────────────────────────────────────

        [TestMethod]
        public async Task MatchingRowsArePaired()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""", """{"category":"shoes"}""");

            var rows = await Join(Async("bikes", "shoes"), executor);

            rows.Should().Equal("bikes/bikes", "shoes/shoes");
        }

        [TestMethod]
        public async Task ABuildRowWithNoMatchYieldsNothing()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await Join(Async("bikes", "hats"), executor);

            rows.Should().Equal("bikes/bikes");
        }

        [TestMethod]
        public async Task EveryMatchingDocumentIsPaired()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""", """{"category":"bikes"}""");

            var rows = await Join(Async("bikes"), executor);

            rows.Should().Equal("bikes/bikes", "bikes/bikes");
        }

        /// <summary>
        /// A key that is an <see cref="int"/> on one side and a <see cref="long"/> on the other still
        /// matches.
        /// </summary>
        /// <remarks>
        /// The two sides arrive by different routes and JSON has one numeric type, so the boxes will
        /// not agree even when the values do. Comparing them directly would drop matching rows and
        /// report a smaller result as though it were the answer — which is why the keys are normalised
        /// rather than compared as they arrive.
        /// </remarks>
        [TestMethod]
        public async Task NumericKeysMatchAcrossTheirClrTypes()
        {
            var executor = new RecordingExecutor("""{"n":7}""");

            var rows = await Collect(CosmosLookup.JoinAsync<int, long, string>(
                Async(7),
                executor,
                Query(),
                "@k",
                3,
                b => b,
                element => element.GetProperty("n").GetInt64(),
                p => p,
                (b, p) => $"{b}/{p}"));

            rows.Should().Equal("7/7");
        }

        [TestMethod]
        public void NormalizeReducesEveryNumberToTheSameForm()
        {
            CosmosLookup.Normalize(7).Should().Be(CosmosLookup.Normalize(7L));
            CosmosLookup.Normalize(7d).Should().Be(CosmosLookup.Normalize((decimal)7));
            CosmosLookup.Normalize("7").Should().NotBe(CosmosLookup.Normalize(7));
            CosmosLookup.Normalize(null).Should().BeNull();
        }

        // ── Remembering ───────────────────────────────────────────────────────────

        /// <remarks>
        /// Deduplication only reaches within a batch. Reference data is looked up repeatedly across
        /// them by definition, and this is where the saving is: a thousand build rows over one key
        /// should ask once, not once per batch.
        /// </remarks>
        [TestMethod]
        public async Task AKeyRepeatedAcrossBatchesIsFetchedOnce()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            var rows = await Join(Async("bikes", "bikes", "bikes"), executor, batchSize: 1, cacheSize: 16);

            executor.Executed.Should().ContainSingle("the second and third batches should be answered from memory");
            rows.Should().HaveCount(3, "every build row still joins");
        }

        /// <remarks>
        /// The case a cache most needs to hold. Without remembering absence, a key the container has
        /// nothing for is asked about again in every batch that mentions it, and told nothing again.
        /// </remarks>
        [TestMethod]
        public async Task AKeyWithNoMatchIsRememberedToo()
        {
            var executor = new RecordingExecutor();

            var rows = await Join(Async("hats", "hats", "hats"), executor, batchSize: 1, cacheSize: 16);

            executor.Executed.Should().ContainSingle("absence is an answer, and it is worth keeping");
            rows.Should().BeEmpty();
        }

        [TestMethod]
        public async Task WithoutACacheEveryBatchFetches()
        {
            var executor = new RecordingExecutor("""{"category":"bikes"}""");

            await Join(Async("bikes", "bikes", "bikes"), executor, batchSize: 1, cacheSize: 0);

            executor.Executed.Should().HaveCount(3);
        }

        /// <remarks>
        /// The bound is what stops a large build side from being remembered whole. Filled and then
        /// left alone rather than evicted — nothing here knows which key is worth keeping, and a wrong
        /// eviction costs a request.
        /// </remarks>
        [TestMethod]
        public async Task TheCacheStopsGrowingAtItsBound()
        {
            var executor = new RecordingExecutor();

            // One key fills the cache; the second is never remembered, so it is asked for every time.
            await Join(Async("a", "b", "a", "b"), executor, batchSize: 1, cacheSize: 1);

            var asked = executor.Executed.SelectMany(Keys).Distinct().ToList();
            asked.Should().BeEquivalentTo(new object?[] { "a", "b" });

            executor.Executed.Should().HaveCount(3, "a is remembered after the first batch; b never is");
        }

        // ── The cache across executions ───────────────────────────────────────────

        /// <remarks>
        /// The point of the whole feature: a second execution over the same keys and the same
        /// statement asks the service nothing.
        /// </remarks>
        [TestMethod]
        public async Task ASecondExecutionIsServedFromTheSharedCache()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            var first = new RecordingExecutor("""{"category":"bikes"}""");
            var one = await Join(Async("bikes"), first, shared: shared);

            var second = new RecordingExecutor("""{"category":"bikes"}""");
            var two = await Join(Async("bikes"), second, shared: shared);

            first.Executed.Should().ContainSingle();
            second.Executed.Should().BeEmpty("the answer was remembered across executions");
            two.Should().Equal(one);
        }

        [TestMethod]
        public async Task AbsenceIsSharedAcrossExecutions()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            await Join(Async("missing"), new RecordingExecutor(), shared: shared);

            var second = new RecordingExecutor();
            var rows = await Join(Async("missing"), second, shared: shared);

            second.Executed.Should().BeEmpty("nothing was remembered as the answer, and remembered is remembered");
            rows.Should().BeEmpty();
        }

        /// <remarks>
        /// Two plans rendering different statements must not share answers: the entries are keyed by
        /// the statement as well as the key.
        /// </remarks>
        [TestMethod]
        public async Task DifferentStatementsDoNotShareTheCache()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            await Join(Async("bikes"), new RecordingExecutor("""{"category":"bikes"}"""), shared: shared);

            var other = new CosmosQuery("SELECT VALUE c FROM archive c WHERE c.category IN (@k0, @k1, @k2)", Array.Empty<CosmosParameter>());
            var second = new RecordingExecutor("""{"category":"bikes"}""");
            await Join(Async("bikes"), second, query: other, shared: shared);

            second.Executed.Should().ContainSingle("an answer for one statement is no answer for another");
        }

        /// <remarks>
        /// The per-join cache holds built rows and the shared cache holds JSON; a batch answered from
        /// the shared cache still fills the per-join one, so the same key in a later batch of the
        /// same join costs neither a request nor a rebuild.
        /// </remarks>
        [TestMethod]
        public async Task ASharedHitStillFillsThePerJoinCache()
        {
            var shared = new CosmosLookupCache(100, TimeSpan.FromMinutes(5));

            await Join(Async("bikes"), new RecordingExecutor("""{"category":"bikes"}"""), shared: shared);

            var second = new RecordingExecutor();
            var rows = await Join(Async("bikes", "bikes"), second, batchSize: 1, cacheSize: 10, shared: shared);

            second.Executed.Should().BeEmpty();
            rows.Should().Equal("bikes/bikes", "bikes/bikes");
        }

        // ── Binding ───────────────────────────────────────────────────────────────

        [TestMethod]
        public void BindKeepsTheStatementsOwnParameters()
        {
            var query = new CosmosQuery("SELECT VALUE c FROM products c WHERE c.price > @p0 AND c.category IN (@k0, @k1)", new[] { new CosmosParameter("@p0", 100) });

            var bound = CosmosLookup.Bind(query, "@k", 2, new object?[] { "bikes" });

            bound.Parameters.Should().Equal(
                new CosmosParameter("@p0", 100),
                new CosmosParameter("@k0", "bikes"),
                new CosmosParameter("@k1", "bikes"));
        }

        [TestMethod]
        public void BindRefusesMoreKeysThanTheStatementCarries()
        {
            var bind = () => CosmosLookup.Bind(Query(), "@k", 2, new object?[] { "a", "b", "c" });

            bind.Should().Throw<ArgumentException>();
        }

    }

}
