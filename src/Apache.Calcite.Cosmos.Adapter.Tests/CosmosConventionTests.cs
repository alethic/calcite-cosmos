using System.Linq;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    [TestClass]
    public class CosmosConventionTests
    {

        [TestMethod]
        public void ConventionIsNamedForItsContainer()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));
            convention.getName().Should().Be("COSMOS.products");
        }

        /// <remarks>
        /// A convention is bound to one container, so two containers yield two conventions and the
        /// planner inserts converters between them.
        /// </remarks>
        [TestMethod]
        public void DistinctContainersYieldDistinctConventions()
        {
            var a = CosmosConvention.Create(new CosmosContainerMetadata("products"));
            var b = CosmosConvention.Create(new CosmosContainerMetadata("orders"));

            a.getName().Should().NotBe(b.getName());
        }

        [TestMethod]
        public void ContainerMetadataIsReachableFromTheConvention()
        {
            var container = new CosmosContainerMetadata("products", new[] { "/tenant" });
            CosmosConvention.Create(container).Container.Should().BeSameAs(container);
        }

        [TestMethod]
        public void RulesAreBoundToTheConvention()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));
            CosmosRules.GetRules(convention).Should().NotBeEmpty();
        }

        /// <remarks>
        /// Joins, set operations, and values have no Cosmos equivalent. Their absence is a design
        /// commitment, not an omission.
        /// </remarks>
        [TestMethod]
        public void NoJoinOrSetOperationRulesAreRegistered()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));
            var names = CosmosRules.GetRules(convention).Select(x => x.GetType().Name).ToList();

            names.Should().NotContain(x => x.Contains("Join"));
            names.Should().NotContain(x => x.Contains("Union"));
            names.Should().NotContain(x => x.Contains("Intersect"));
            names.Should().NotContain(x => x.Contains("Minus"));
            names.Should().NotContain(x => x.Contains("Values"));
        }

    }

}
