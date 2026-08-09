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

        /// <summary>
        /// Nothing is converted <em>into</em> the Cosmos convention that Cosmos cannot express.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Joins, set operations and values have no Cosmos equivalent, and their absence is a design
        /// commitment rather than an omission. The check is on the out-convention rather than on the
        /// rule's name, because a name says what a rule is about and the convention says what it
        /// promises the service can run.
        /// </para>
        /// <para>
        /// <c>CosmosLookupJoinRule</c> is the reason that distinction now matters. It is a join rule,
        /// and it converts into <c>ClrAsyncEnumerableConvention</c> — the join is still performed
        /// outside the service, and what reaches the statement is only a restriction to the keys the
        /// other side has. A rule converting a join <em>into</em> the Cosmos convention would be
        /// claiming the service can join, which it cannot.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void NothingInexpressibleIsConvertedIntoTheConvention()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));

            var into = CosmosRules.GetRules(convention)
                .OfType<org.apache.calcite.rel.convert.ConverterRule>()
                .Where(x => ReferenceEquals(x.getOutConvention(), convention))
                .Select(x => x.GetType().Name)
                .ToList();

            into.Should().NotBeEmpty("the convention is reached by rules, so an empty list would mean the check is vacuous");

            foreach (var forbidden in new[] { "Join", "Union", "Intersect", "Minus", "Values" })
                into.Should().NotContain(x => x.Contains(forbidden), "{0} has no Cosmos equivalent", forbidden);
        }

        /// <remarks>
        /// The lookup join leaves the convention, exactly as the converter does, and for the same
        /// reason: below it is a statement, above it are rows.
        /// </remarks>
        [TestMethod]
        public void TheLookupJoinConvertsOutOfTheConvention()
        {
            var convention = CosmosConvention.Create(new CosmosContainerMetadata("products"));

            var rule = CosmosRules.GetRules(convention)
                .OfType<Adapter.Rel.Convert.CosmosLookupJoinRule>()
                .Should().ContainSingle().Subject;

            rule.getOutConvention().Should().BeSameAs(Apache.Calcite.Extensions.Adapter.AsyncEnumerable.ClrAsyncEnumerableConvention.Instance);
        }

    }

}
