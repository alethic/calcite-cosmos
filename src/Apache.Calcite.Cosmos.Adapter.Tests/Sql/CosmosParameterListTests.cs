using System;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    [TestClass]
    public class CosmosParameterListTests
    {

        [TestMethod]
        public void NamesAreAssignedInOrder()
        {
            var p = new CosmosParameterList();

            p.Add("a").Should().Be("@p0");
            p.Add(2).Should().Be("@p1");
            p.Add(null).Should().Be("@p2");
            p.Count.Should().Be(3);
        }

        [TestMethod]
        public void ValuesArePreservedInOrder()
        {
            var p = new CosmosParameterList();
            p.Add("a");
            p.Add(2);

            p.Parameters.Should().SatisfyRespectively(
                x => { x.Name.Should().Be("@p0"); x.Value.Should().Be("a"); },
                x => { x.Name.Should().Be("@p1"); x.Value.Should().Be(2); });
        }

        [TestMethod]
        public void EqualValuesAreBoundSeparately()
        {
            var p = new CosmosParameterList();

            p.Add("a").Should().Be("@p0");
            p.Add("a").Should().Be("@p1");
        }

        [TestMethod]
        public void PrefixIsHonored()
        {
            var p = new CosmosParameterList("@arg");
            p.Add(1).Should().Be("@arg0");
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("p")]
        public void InvalidPrefixIsRejected(string prefix)
        {
            var act = () => new CosmosParameterList(prefix);
            act.Should().Throw<ArgumentException>();
        }

    }

}
