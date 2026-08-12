using System;
using System.Collections.Generic;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    [TestClass]
    public class CosmosLookupCacheTests
    {

        /// <summary>
        /// A clock the test moves by hand.
        /// </summary>
        sealed class ManualClock : TimeProvider
        {

            public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

            public override DateTimeOffset GetUtcNow() => Now;

        }

        static IReadOnlyList<JsonElement> Rows(params string[] documents)
        {
            var rows = new List<JsonElement>();
            foreach (var document in documents)
                rows.Add(JsonDocument.Parse(document).RootElement.Clone());

            return rows;
        }

        [TestMethod]
        public void RemembersWhatWasSet()
        {
            var cache = new CosmosLookupCache(10, TimeSpan.FromMinutes(5));

            cache.Set("s", "bikes", Rows("""{"v":1}"""));

            cache.TryGet("s", "bikes", out var rows).Should().BeTrue();
            rows.Should().ContainSingle().Which.GetProperty("v").GetInt32().Should().Be(1);
        }

        /// <remarks>
        /// Absence is the answer a cache most needs to hold: without it, every batch mentioning the
        /// key asks again and is told nothing again.
        /// </remarks>
        [TestMethod]
        public void RemembersAbsence()
        {
            var cache = new CosmosLookupCache(10, TimeSpan.FromMinutes(5));

            cache.Set("s", "missing", Array.Empty<JsonElement>());

            cache.TryGet("s", "missing", out var rows).Should().BeTrue();
            rows.Should().BeEmpty();
        }

        [TestMethod]
        public void AnEntryExpiresAfterItsTimeToLive()
        {
            var clock = new ManualClock();
            var cache = new CosmosLookupCache(10, TimeSpan.FromMinutes(5), clock);

            cache.Set("s", "bikes", Rows("""{"v":1}"""));

            clock.Now += TimeSpan.FromMinutes(4);
            cache.TryGet("s", "bikes", out _).Should().BeTrue("four minutes is within the policy");

            clock.Now += TimeSpan.FromMinutes(2);
            cache.TryGet("s", "bikes", out _).Should().BeFalse("six minutes is beyond it");
        }

        /// <remarks>
        /// Two plans rendering different statements must not share answers, and neither may two
        /// bindings of the same text — a filter's parameter value is part of the identity.
        /// </remarks>
        [TestMethod]
        public void StatementsDoNotShareEntries()
        {
            var cache = new CosmosLookupCache(10, TimeSpan.FromMinutes(5));

            cache.Set("SELECT a", "bikes", Rows("""{"v":1}"""));

            cache.TryGet("SELECT b", "bikes", out _).Should().BeFalse();
        }

        [TestMethod]
        public void TheIdentityCarriesTheNonKeyParameters()
        {
            var one = new CosmosQuery("SELECT * FROM c WHERE c.p > @p0", new[] { new CosmosParameter("@p0", 5L) });
            var two = new CosmosQuery("SELECT * FROM c WHERE c.p > @p0", new[] { new CosmosParameter("@p0", "5") });

            CosmosLookupCache.Statement(one).Should().NotBe(CosmosLookupCache.Statement(two),
                "a long and a string that print alike must not share an entry");
        }

        /// <remarks>
        /// Expiry is the only eviction: a full cache purges what has expired, and otherwise declines
        /// the new entry rather than evicting something for it.
        /// </remarks>
        [TestMethod]
        public void AFullCacheDeclinesRatherThanEvicts()
        {
            var cache = new CosmosLookupCache(2, TimeSpan.FromMinutes(5));

            cache.Set("s", "a", Rows("""{"v":1}""", """{"v":2}"""));
            cache.Set("s", "b", Rows("""{"v":3}"""));

            cache.Rows.Should().Be(2, "the second entry did not fit and was not kept");
            cache.TryGet("s", "a", out _).Should().BeTrue();
            cache.TryGet("s", "b", out _).Should().BeFalse();
        }

        [TestMethod]
        public void ExpiredEntriesMakeRoom()
        {
            var clock = new ManualClock();
            var cache = new CosmosLookupCache(2, TimeSpan.FromMinutes(5), clock);

            cache.Set("s", "a", Rows("""{"v":1}""", """{"v":2}"""));

            clock.Now += TimeSpan.FromMinutes(6);
            cache.Set("s", "b", Rows("""{"v":3}"""));

            cache.TryGet("s", "b", out _).Should().BeTrue("the expired entry was purged to make room");
            cache.TryGet("s", "a", out _).Should().BeFalse();
        }

        /// <remarks>
        /// An absence entry counts as one row: absence must not be free to hold without limit.
        /// </remarks>
        [TestMethod]
        public void AbsenceCountsAgainstTheBound()
        {
            var cache = new CosmosLookupCache(1, TimeSpan.FromMinutes(5));

            cache.Set("s", "a", Array.Empty<JsonElement>());

            cache.Rows.Should().Be(1);
        }

        [TestMethod]
        public void ClearForgetsEverything()
        {
            var cache = new CosmosLookupCache(10, TimeSpan.FromMinutes(5));

            cache.Set("s", "a", Rows("""{"v":1}"""));
            cache.Clear();

            cache.Rows.Should().Be(0);
            cache.TryGet("s", "a", out _).Should().BeFalse();
        }

        [TestMethod]
        public void ResettingAKeyReplacesItsRows()
        {
            var cache = new CosmosLookupCache(10, TimeSpan.FromMinutes(5));

            cache.Set("s", "a", Rows("""{"v":1}""", """{"v":2}"""));
            cache.Set("s", "a", Rows("""{"v":3}"""));

            cache.Rows.Should().Be(1);
            cache.TryGet("s", "a", out var rows).Should().BeTrue();
            rows.Should().ContainSingle().Which.GetProperty("v").GetInt32().Should().Be(3);
        }

        sealed class NullWriter : ICosmosItemWriter
        {

            public System.Threading.Tasks.Task CreateItemAsync(byte[] document, Microsoft.Azure.Cosmos.PartitionKey partitionKey, System.Threading.CancellationToken cancellationToken = default) =>
                System.Threading.Tasks.Task.CompletedTask;

            public System.Threading.Tasks.Task<bool> DeleteItemAsync(string id, Microsoft.Azure.Cosmos.PartitionKey partitionKey, System.Threading.CancellationToken cancellationToken = default) =>
                System.Threading.Tasks.Task.FromResult(true);

            public System.Threading.Tasks.Task<bool> ReplaceItemAsync(byte[] document, string id, Microsoft.Azure.Cosmos.PartitionKey partitionKey, System.Threading.CancellationToken cancellationToken = default) =>
                System.Threading.Tasks.Task.FromResult(true);

            public System.Threading.Tasks.Task<bool> DeletePartitionAsync(Microsoft.Azure.Cosmos.PartitionKey partitionKey, System.Threading.CancellationToken cancellationToken = default) =>
                System.Threading.Tasks.Task.FromResult(true);

            public System.Threading.Tasks.Task<bool> SupportsPartitionDeleteAsync(System.Threading.CancellationToken cancellationToken = default) =>
                System.Threading.Tasks.Task.FromResult(false);

        }

        static async IAsyncEnumerable<object?[]> OneRow()
        {
            await System.Threading.Tasks.Task.Yield();
            yield return new object?[] { null, "1", null, null, "bikes" };
        }

        /// <remarks>
        /// The container changed, so what the cache remembers about it may be wrong. A write from
        /// outside the process is the TTL's problem; one through the adapter is this test's.
        /// </remarks>
        [TestMethod]
        public async System.Threading.Tasks.Task AWriteThroughTheAdapterClearsTheCache()
        {
            var cache = new CosmosLookupCache(10, TimeSpan.FromMinutes(5));
            cache.Set("s", "bikes", Rows("""{"v":1}"""));

            var write = new CosmosWrite(CosmosWriteOperation.Insert, ["_MAP", "id", "_ts", "_etag", "category"], ["/category"]);

            await foreach (var _ in CosmosSequences.WriteAsync<object?[], long>(OneRow(), new NullWriter(), write, r => r!, c => c, cache))
            {
            }

            cache.Rows.Should().Be(0);
        }

    }

}
