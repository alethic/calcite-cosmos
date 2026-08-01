using System.Collections.Generic;
using System.Collections.ObjectModel;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    [TestClass]
    public class CosmosContainerMetadataReaderTests
    {

        static Collection<CompositePath> Composite(params (string Path, CompositePathSortOrder Order)[] paths)
        {
            var collection = new Collection<CompositePath>();
            foreach (var (path, order) in paths)
                collection.Add(new CompositePath { Path = path, Order = order });

            return collection;
        }

        [TestMethod]
        public void ContainerIdBecomesTheName()
        {
            var properties = new ContainerProperties("products", "/pk");
            CosmosContainerMetadataReader.FromProperties(properties).Name.Should().Be("products");
        }

        [TestMethod]
        public void SinglePartitionKeyIsRead()
        {
            var properties = new ContainerProperties("products", "/category");
            CosmosContainerMetadataReader.FromProperties(properties).PartitionKeyPaths.Should().Equal("/category");
        }

        [TestMethod]
        public void HierarchicalPartitionKeyIsReadInOrder()
        {
            var properties = new ContainerProperties("products", new List<string> { "/tenant", "/user" });
            CosmosContainerMetadataReader.FromProperties(properties).PartitionKeyPaths.Should().Equal("/tenant", "/user");
        }

        [TestMethod]
        public void CompositeIndexesAreRead()
        {
            var properties = new ContainerProperties("products", "/pk");
            properties.IndexingPolicy.CompositeIndexes.Add(Composite(
                ("/name", CompositePathSortOrder.Ascending),
                ("/price", CompositePathSortOrder.Descending)));

            var index = CosmosContainerMetadataReader.FromProperties(properties).CompositeIndexes.Should().ContainSingle().Subject;

            index.Paths.Should().SatisfyRespectively(
                p => { p.Path.Should().Be("/name"); p.Descending.Should().BeFalse(); },
                p => { p.Path.Should().Be("/price"); p.Descending.Should().BeTrue(); });
        }

        [TestMethod]
        public void SeveralCompositeIndexesAreRead()
        {
            var properties = new ContainerProperties("products", "/pk");
            properties.IndexingPolicy.CompositeIndexes.Add(Composite(("/a", CompositePathSortOrder.Ascending), ("/b", CompositePathSortOrder.Ascending)));
            properties.IndexingPolicy.CompositeIndexes.Add(Composite(("/c", CompositePathSortOrder.Ascending), ("/d", CompositePathSortOrder.Ascending)));

            CosmosContainerMetadataReader.FromProperties(properties).CompositeIndexes.Should().HaveCount(2);
        }

        /// <remarks>
        /// A one-path definition cannot serve a multi-key sort, which is the only thing composite
        /// indexes are consulted for. Skipping it is preferable to refusing to build metadata for
        /// an otherwise-valid container.
        /// </remarks>
        [TestMethod]
        public void SinglePathCompositeIndexIsSkippedRatherThanRejected()
        {
            var properties = new ContainerProperties("products", "/pk");
            properties.IndexingPolicy.CompositeIndexes.Add(Composite(("/name", CompositePathSortOrder.Ascending)));

            CosmosContainerMetadataReader.FromProperties(properties).CompositeIndexes.Should().BeEmpty();
        }

        /// <remarks>
        /// Composite index paths conventionally omit the trailing specifier, but stripping it
        /// defensively keeps comparison against <c>CosmosPath.ToPolicyPath</c> independent of
        /// which form the service returned.
        /// </remarks>
        [TestMethod]
        public void TrailingPathSpecifiersAreStripped()
        {
            var properties = new ContainerProperties("products", "/pk");
            properties.IndexingPolicy.CompositeIndexes.Add(Composite(
                ("/name/?", CompositePathSortOrder.Ascending),
                ("/inventory/quantity/?", CompositePathSortOrder.Ascending)));

            var index = CosmosContainerMetadataReader.FromProperties(properties).CompositeIndexes[0];
            index.Paths[0].Path.Should().Be("/name");
            index.Paths[1].Path.Should().Be("/inventory/quantity");
        }

        [TestMethod]
        public void NoCompositeIndexesYieldsAnEmptyList()
        {
            var properties = new ContainerProperties("products", "/pk");
            CosmosContainerMetadataReader.FromProperties(properties).CompositeIndexes.Should().BeEmpty();
        }

        /// <remarks>
        /// The read metadata must drive the sort guard, so check it end to end rather than only
        /// asserting the shape.
        /// </remarks>
        [TestMethod]
        public void ReadMetadataDrivesTheSortGuard()
        {
            var properties = new ContainerProperties("products", "/pk");
            properties.IndexingPolicy.CompositeIndexes.Add(Composite(
                ("/name", CompositePathSortOrder.Ascending),
                ("/price", CompositePathSortOrder.Ascending)));

            var container = CosmosContainerMetadataReader.FromProperties(properties);

            container.IsSortSupported(new[]
            {
                new CosmosSortKey("/name", false),
                new CosmosSortKey("/price", false),
            }).Should().BeTrue();

            container.IsSortSupported(new[]
            {
                new CosmosSortKey("/price", false),
                new CosmosSortKey("/name", false),
            }).Should().BeFalse();
        }

    }

}
