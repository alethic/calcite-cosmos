using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Whether a path is covered by the container's index. This bears on cost only — an unindexed
    /// path still queries, it just scans.
    /// </summary>
    [TestClass]
    public class CosmosIndexingPolicyTests
    {

        static CosmosContainerMetadata Policy(string[] included, string[] excluded) =>
            new("products", new[] { "/category" }, null, included, excluded);

        [TestMethod]
        public void DefaultPolicyIndexesEverything()
        {
            var container = new CosmosContainerMetadata("products");

            container.IsPathIndexed("/name").Should().BeTrue();
            container.IsPathIndexed("/inventory/quantity").Should().BeTrue();
        }

        [TestMethod]
        public void RootInclusionIndexesEverything()
        {
            var container = Policy(new[] { "/*" }, new string[0]);

            container.IsPathIndexed("/name").Should().BeTrue();
            container.IsPathIndexed("/deeply/nested/path").Should().BeTrue();
        }

        [TestMethod]
        public void ExplicitlyExcludedPathIsNotIndexed()
        {
            var container = Policy(new[] { "/*" }, new[] { "/notes/?" });

            container.IsPathIndexed("/notes").Should().BeFalse();
            container.IsPathIndexed("/name").Should().BeTrue();
        }

        [TestMethod]
        public void ExcludedSubtreeCoversPathsBeneathIt()
        {
            var container = Policy(new[] { "/*" }, new[] { "/blob/*" });

            container.IsPathIndexed("/blob").Should().BeFalse();
            container.IsPathIndexed("/blob/inner").Should().BeFalse();
            container.IsPathIndexed("/other").Should().BeTrue();
        }

        /// <remarks>
        /// The documented precedence: a deeper inclusion overrides a shallower exclusion.
        /// </remarks>
        [TestMethod]
        public void DeeperInclusionOverridesShallowerExclusion()
        {
            var container = Policy(new[] { "/model/manufacturer/*" }, new[] { "/model/*" });

            container.IsPathIndexed("/model/manufacturer").Should().BeTrue();
            container.IsPathIndexed("/model/manufacturer/name").Should().BeTrue();
            container.IsPathIndexed("/model/other").Should().BeFalse();
        }

        /// <remarks>
        /// At equal depth <c>/?</c> is more precise than <c>/*</c>.
        /// </remarks>
        [TestMethod]
        public void ExactPatternOutranksSubtreeAtTheSameDepth()
        {
            Policy(new[] { "/a/?" }, new[] { "/a/*" }).IsPathIndexed("/a").Should().BeTrue();
            Policy(new[] { "/a/*" }, new[] { "/a/?" }).IsPathIndexed("/a").Should().BeFalse();
        }

        [TestMethod]
        public void PathMatchingNoInclusionIsNotIndexed()
        {
            var container = Policy(new[] { "/name/?" }, new string[0]);

            container.IsPathIndexed("/name").Should().BeTrue();
            container.IsPathIndexed("/other").Should().BeFalse();
        }

        /// <remarks>
        /// The service always indexes these and does not permit excluding them.
        /// </remarks>
        [TestMethod]
        public void IdAndTimestampAreAlwaysIndexed()
        {
            var container = Policy(new string[0], new[] { "/*" });

            container.IsPathIndexed("/id").Should().BeTrue();
            container.IsPathIndexed("/_ts").Should().BeTrue();
            container.IsPathIndexed("/_etag").Should().BeFalse();
        }

        [TestMethod]
        public void PolicyPathsAreReadFromTheContainerDefinition()
        {
            var properties = new ContainerProperties("products", "/pk");
            properties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = "/*" });
            properties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/blob/*" });

            var container = CosmosContainerMetadataReader.FromProperties(properties);

            container.IncludedPaths.Should().Contain("/*");
            container.ExcludedPaths.Should().Contain("/blob/*");
            container.IsPathIndexed("/blob/inner").Should().BeFalse();
            container.IsPathIndexed("/name").Should().BeTrue();
        }

    }

}
