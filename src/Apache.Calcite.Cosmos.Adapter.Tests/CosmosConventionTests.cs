using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    [TestClass]
    public class CosmosConventionTests
    {

        [TestMethod]
        public void CanCreateConvention()
        {
            var convention = CosmosConvention.Create("TEST");
            convention.getName().Should().Be("COSMOS.TEST");
        }

    }

}
