using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.rel;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// The statistics a container can honestly supply. Everything here comes from the container
    /// definition; nothing is inferred from documents.
    /// </summary>
    [TestClass]
    public class CosmosTableStatisticTests
    {

        /// <summary>
        /// The field ordinals a distribution names.
        /// </summary>
        static System.Collections.Generic.List<int> Ordinals(RelDistribution distribution)
        {
            var ordinals = new System.Collections.Generic.List<int>();

            var keys = distribution.getKeys();
            for (var i = 0; i < keys.size(); i++)
                ordinals.Add(((java.lang.Integer)keys.get(i)).intValue());

            return ordinals;
        }

        static CosmosCompositeIndex Index(params (string Path, bool Descending)[] paths)
        {
            var list = new System.Collections.Generic.List<CosmosCompositeIndexPath>();
            foreach (var (path, descending) in paths)
                list.Add(new CosmosCompositeIndexPath(path, descending));

            return new CosmosCompositeIndex(list);
        }

        // Row type ordinals: 0 _MAP, 1 id, 2 _ts, 3 _etag, then declared paths.

        [TestMethod]
        public void PromotedColumnsResolveToOrdinals()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/category" }));

            table.GetColumnOrdinal("/id").Should().Be(1);
            table.GetColumnOrdinal("/_ts").Should().Be(2);
            table.GetColumnOrdinal("/_etag").Should().Be(3);
            table.GetColumnOrdinal("/category").Should().Be(4);
        }

        [TestMethod]
        public void UnpromotedPathsHaveNoOrdinal()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/category" }));

            table.GetColumnOrdinal("/name").Should().Be(-1);
            table.GetColumnOrdinal("/inventory/quantity").Should().Be(-1);
        }

        /// <remarks>
        /// <c>id</c> is unique within a logical partition, so partition key plus <c>id</c> is
        /// unique across the container.
        /// </remarks>
        [TestMethod]
        public void PartitionKeyPlusIdIsAKey()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/category" }));
            var keys = table.getStatistic().getKeys();

            keys.size().Should().Be(1);
            ((ImmutableBitSet)keys.get(0)).Should().Be(ImmutableBitSet.of(new[] { 1, 4 }));
        }

        [TestMethod]
        public void HierarchicalPartitionKeyContributesEveryPath()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/tenant", "/user" }));
            var keys = table.getStatistic().getKeys();

            // 0 _MAP, 1 id, 2 _ts, 3 _etag, 4 tenant, 5 user
            ((ImmutableBitSet)keys.get(0)).Should().Be(ImmutableBitSet.of(new[] { 1, 4, 5 }));
        }

        /// <remarks>
        /// A nested partition key path has no column ordinal, so the key cannot be expressed.
        /// Claiming one anyway would be a silently wrong plan.
        /// </remarks>
        [TestMethod]
        public void NestedPartitionKeyYieldsNoKey()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/inventory/sku" }));
            table.getStatistic().getKeys().size().Should().Be(0);
        }

        [TestMethod]
        public void UndeclaredPartitionKeyYieldsNoKey()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products"));
            table.getStatistic().getKeys().size().Should().Be(0);
        }

        /// <remarks>
        /// A container is hash-distributed by its partition key — the service's own routing, not an
        /// inference about the data — and saying so hands the planner the fact
        /// <c>CosmosFilter.computeSelfCost</c> otherwise spends privately when it discounts a
        /// pinned key by the partition count.
        /// </remarks>
        [TestMethod]
        public void ThePartitionKeyIsReportedAsAHashDistribution()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/category" }));
            var distribution = table.getStatistic().getDistribution();

            distribution.getType().Should().Be(RelDistribution.Type.HASH_DISTRIBUTED);
            Ordinals(distribution).Should().Equal(4);
        }

        [TestMethod]
        public void AHierarchicalKeyDistributesOverEveryPath()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/tenant", "/user" }));
            var distribution = table.getStatistic().getDistribution();

            // 0 _MAP, 1 id, 2 _ts, 3 _etag, 4 tenant, 5 user
            Ordinals(distribution).Should().Equal(4, 5);
        }

        /// <remarks>
        /// A distribution is expressed over field ordinals, so a nested path — which has no column —
        /// yields <c>RANDOM</c> rather than a distribution over ordinals the row type does not have.
        /// The same promotion rule the key obeys, for the same reason.
        /// </remarks>
        [TestMethod]
        public void ANestedPartitionKeyDistributesRandomly()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/inventory/sku" }));

            table.getStatistic().getDistribution().getType().Should().Be(RelDistribution.Type.RANDOM_DISTRIBUTED);
        }

        /// <remarks>
        /// The distribution says how rows are spread, never what order they arrive in — which is why
        /// declaring one is safe where declaring a collation would licence dropping a <c>Sort</c>.
        /// </remarks>
        [TestMethod]
        public void NoCollationIsClaimedAlongsideTheDistribution()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/category" }));

            table.getStatistic().getCollations().size().Should().Be(0);
        }

        /// <remarks>
        /// <para>
        /// A composite index is <b>not</b> a collation, and reporting one was a defect. A statistic's
        /// collations are the order a scan's rows already arrive in — <c>RelOptTableImpl</c> hands them
        /// to <c>RelMdCollation</c> as the collation of the scan — so claiming one licences the planner
        /// to drop a <c>Sort</c> that asked for exactly that order. Cosmos guarantees no order without
        /// an <c>ORDER BY</c>, whatever is indexed.
        /// </para>
        /// <para>
        /// What a composite index decides is whether a multi-key sort is <em>legal</em>, and that
        /// question belongs to the rule that pushes the sort — where
        /// <c>CosmosContainerMetadata.IsSortSupported</c> answers it. Calcite's Cassandra adapter puts
        /// its clustering order in the rule for the same reason, and that really is the storage order.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void ACompositeIndexIsNotACollation()
        {
            var container = new CosmosContainerMetadata(
                "products",
                new[] { "/category" },
                new[] { Index(("/id", false), ("/_ts", true)) });

            new CosmosTable(container).getStatistic().getCollations().size().Should().Be(0);

            // And is still what decides whether such a sort may be pushed.
            container.IsSortSupported(new[]
            {
                new CosmosSortKey("/id", false),
                new CosmosSortKey("/_ts", true),
            }).Should().BeTrue();
        }

        /// <remarks>
        /// A composite index over a path inside the map column names nothing the planner can address,
        /// and remains valid for the sort guard regardless.
        /// </remarks>
        [TestMethod]
        public void CompositeIndexOverUnpromotedPathsIsStillUsableForTheSortGuard()
        {
            var container = new CosmosContainerMetadata(
                "products",
                new[] { "/category" },
                new[] { Index(("/name", false), ("/price", false)) });

            new CosmosTable(container).getStatistic().getCollations().size().Should().Be(0);

            // Still usable for deciding whether the sort is legal.
            container.IsSortSupported(new[]
            {
                new CosmosSortKey("/name", false),
                new CosmosSortKey("/price", false),
            }).Should().BeTrue();
        }

        [TestMethod]
        public void RowCountIsNotInvented()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("products", new[] { "/category" }));
            table.getStatistic().getRowCount().Should().BeNull();
        }


        // ── Statistics from the service ───────────────────────────────────────────

        /// <remarks>
        /// A container built from a definition alone has no row count, and the planner compares plans
        /// without one exactly as it did before. Nothing here samples documents.
        /// </remarks>
        [TestMethod]
        public void WithoutStatisticsTheRowCountIsUnknown()
        {
            new CosmosTable(new CosmosContainerMetadata("products")).getStatistic().getRowCount().Should().BeNull();
        }

        [TestMethod]
        public void AMeasuredRowCountIsReported()
        {
            var container = new CosmosContainerMetadata("products").WithStatistics(new CosmosContainerStatistics(4200, 8_400_000, 4));

            new CosmosTable(container).getStatistic().getRowCount().doubleValue().Should().Be(4200d);
        }

        /// <remarks>
        /// What a row costs to move, which for a row model carrying whole documents dominates.
        /// </remarks>
        [TestMethod]
        public void AverageDocumentSizeIsDerivedFromTheTotal()
        {
            new CosmosContainerStatistics(100, 50_000, 2).AverageDocumentSizeInBytes.Should().Be(500d);
        }

        [TestMethod]
        public void AnEmptyContainerHasNoAverageDocumentSize()
        {
            new CosmosContainerStatistics(0, 0, 1).AverageDocumentSizeInBytes.Should().Be(0d);
        }


        // ── Statistics are fetched when asked for ─────────────────────────────────

        /// <remarks>
        /// A schema exposes every container of a database — at the account level, of every database —
        /// and each fetch is two round trips. Paying for all of them to plan against one is the wrong
        /// trade, so the provider is invoked when a plan first asks and never for a container nothing
        /// touches. Flink's FLIP-231 collects connector statistics during optimisation for the same
        /// reason.
        /// </remarks>
        [TestMethod]
        public void AStatisticsProviderIsNotInvokedUntilItIsAsked()
        {
            var invocations = 0;

            var container = new CosmosContainerMetadata("products")
                .WithStatisticsProvider(() =>
                {
                    invocations++;
                    return new CosmosContainerStatistics(7, 700, 2);
                });

            var table = new CosmosTable(container);
            invocations.Should().Be(0, "building a table asks nothing of the service");

            table.getStatistic().getRowCount().doubleValue().Should().Be(7d);
            invocations.Should().Be(1);
        }

        [TestMethod]
        public void AStatisticsProviderIsInvokedOnlyOnce()
        {
            var invocations = 0;

            var container = new CosmosContainerMetadata("products")
                .WithStatisticsProvider(() =>
                {
                    invocations++;
                    return new CosmosContainerStatistics(7, 700, 2);
                });

            _ = container.Statistics;
            _ = container.Statistics;
            _ = container.Statistics;

            invocations.Should().Be(1);
        }

        /// <remarks>
        /// An account that cannot answer leaves the planner where it would have been without one,
        /// rather than failing the query — which is what Flink does with an unavailable statistic too.
        /// </remarks>
        [TestMethod]
        public void AProviderReturningNothingLeavesTheRowCountUnknown()
        {
            var container = new CosmosContainerMetadata("products").WithStatisticsProvider(() => null);

            new CosmosTable(container).getStatistic().getRowCount().Should().BeNull();
        }

    }

}