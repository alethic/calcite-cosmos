using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Metadata
{

    /// <summary>
    /// Recovering the partition key a predicate pins. Getting this wrong in the permissive
    /// direction loses rows, so only a conjunction of equalities against constants qualifies.
    /// </summary>
    [TestClass]
    public class CosmosPartitionKeyExtractorTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;

        public CosmosPartitionKeyExtractorTests()
        {
            _rex = new RexBuilder(_types);
        }

        static readonly CosmosPath[] Fields =
        {
            CosmosPath.Root("c"),                             // 0 — map column
            CosmosPath.Root("c").Property("category"),        // 1
            CosmosPath.Root("c").Property("tenant"),          // 2
            CosmosPath.Root("c").Property("id"),              // 3
            CosmosPath.Root("t0").Property("category"),       // 4 — element-relative
        };

        RexNode Ref(int index) => _rex.makeInputRef(_types.createTypeWithNullability(_types.createSqlType(SqlTypeName.ANY), true), index);

        RexNode Str(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

        RexNode Eq(RexNode a, RexNode b) => _rex.makeCall(SqlStdOperatorTable.EQUALS, a, b);

        RexNode And(params RexNode[] operands) => _rex.makeCall(SqlStdOperatorTable.AND, operands);

        RexNode Or(params RexNode[] operands) => _rex.makeCall(SqlStdOperatorTable.OR, operands);

        static CosmosContainerMetadata Container(params string[] partitionKeyPaths) =>
            new("products", partitionKeyPaths);

        static bool Extract(RexNode condition, CosmosContainerMetadata container, out IReadOnlyList<object?> values) =>
            CosmosPartitionKeyExtractor.TryExtract(condition, Fields, container, "c", out values);

        // ── Recovered ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void EqualityOnThePartitionKeyIsRecovered()
        {
            Extract(Eq(Ref(1), Str("bikes")), Container("/category"), out var values).Should().BeTrue();
            values.Should().Equal("bikes");
        }

        [TestMethod]
        public void OperandOrderDoesNotMatter()
        {
            Extract(Eq(Str("bikes"), Ref(1)), Container("/category"), out var values).Should().BeTrue();
            values.Should().Equal("bikes");
        }

        [TestMethod]
        public void PartitionKeyWithinAConjunctionIsRecovered()
        {
            var condition = And(Eq(Ref(3), Str("x")), Eq(Ref(1), Str("bikes")));

            Extract(condition, Container("/category"), out var values).Should().BeTrue();
            values.Should().Equal("bikes");
        }

        [TestMethod]
        public void HierarchicalKeyIsRecoveredInDeclaredOrder()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Eq(Ref(2), Str("acme")));

            Extract(condition, Container("/tenant", "/category"), out var values).Should().BeTrue();
            values.Should().Equal("acme", "bikes");
        }

        [TestMethod]
        public void MapPropertyPathIsRecovered()
        {
            var item = _rex.makeCall(SqlStdOperatorTable.ITEM, Ref(0), Str("region"));

            Extract(Eq(item, Str("emea")), Container("/region"), out var values).Should().BeTrue();
            values.Should().Equal("emea");
        }

        // ── Not recovered ─────────────────────────────────────────────────────────

        /// <remarks>
        /// Under a disjunction an equality does not constrain the predicate, so the query may
        /// match several partitions.
        /// </remarks>
        [TestMethod]
        public void EqualityUnderADisjunctionIsNotRecovered()
        {
            var condition = Or(Eq(Ref(1), Str("bikes")), Eq(Ref(1), Str("shoes")));

            Extract(condition, Container("/category"), out _).Should().BeFalse();
        }

        [TestMethod]
        public void RangePredicateIsNotRecovered()
        {
            var condition = _rex.makeCall(SqlStdOperatorTable.GREATER_THAN, Ref(1), Str("bikes"));

            Extract(condition, Container("/category"), out _).Should().BeFalse();
        }

        [TestMethod]
        public void PartiallyPinnedHierarchicalKeyIsNotRecovered()
        {
            Extract(Eq(Ref(1), Str("bikes")), Container("/tenant", "/category"), out _).Should().BeFalse();
        }

        [TestMethod]
        public void EqualityOnADifferentPropertyIsNotRecovered()
        {
            Extract(Eq(Ref(3), Str("x")), Container("/category"), out _).Should().BeFalse();
        }

        [TestMethod]
        public void EqualityBetweenTwoPathsIsNotRecovered()
        {
            Extract(Eq(Ref(1), Ref(2)), Container("/category"), out _).Should().BeFalse();
        }

        /// <remarks>
        /// A path rooted at an array-traversal alias addresses an element, not the document.
        /// </remarks>
        [TestMethod]
        public void EqualityOnAnUnnestAliasIsNotRecovered()
        {
            Extract(Eq(Ref(4), Str("bikes")), Container("/category"), out _).Should().BeFalse();
        }

        [TestMethod]
        public void ContainerWithNoDeclaredPartitionKeyRecoversNothing()
        {
            Extract(Eq(Ref(1), Str("bikes")), Container(), out _).Should().BeFalse();
        }

    }

}
