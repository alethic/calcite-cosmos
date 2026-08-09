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

        [TestMethod]
        public void CompositeIndexOverPromotedColumnsBecomesACollation()
        {
            var table = new CosmosTable(new CosmosContainerMetadata(
                "products",
                new[] { "/category" },
                new[] { Index(("/id", false), ("/_ts", true)) }));

            var collations = table.getStatistic().getCollations();
            collations.size().Should().Be(1);

            var fields = ((RelCollation)collations.get(0)).getFieldCollations();
            fields.size().Should().Be(2);

            ((RelFieldCollation)fields.get(0)).getFieldIndex().Should().Be(1);
            ((RelFieldCollation)fields.get(0)).getDirection().Should().Be(RelFieldCollation.Direction.ASCENDING);
            ((RelFieldCollation)fields.get(1)).getFieldIndex().Should().Be(2);
            ((RelFieldCollation)fields.get(1)).getDirection().Should().Be(RelFieldCollation.Direction.DESCENDING);
        }

        /// <remarks>
        /// A composite index over a path inside the map column names nothing the planner can
        /// address, so it contributes no collation even though it remains valid for the sort guard.
        /// </remarks>
        [TestMethod]
        public void CompositeIndexOverUnpromotedPathsContributesNoCollation()
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

    }

}