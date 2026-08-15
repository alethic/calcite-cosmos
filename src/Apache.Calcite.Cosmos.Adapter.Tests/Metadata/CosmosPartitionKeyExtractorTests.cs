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

        // ── Through a cast to text ────────────────────────────────────────────────

        RexNode TextCast(RexNode operand) => _rex.makeAbstractCast(_types.createSqlType(SqlTypeName.VARCHAR), operand, false);

        static bool ExtractPrefix(RexNode condition, CosmosContainerMetadata container, out IReadOnlyList<object?> values) =>
            CosmosPartitionKeyExtractor.TryExtractPrefix(condition, Fields, container, "c", out values, out _);

        /// <summary>
        /// A view exposing the partition key gives it a SQL type, which over this row model means a
        /// cast — and losing single-partition execution to that is the largest cost there is to lose
        /// silently.
        /// </summary>
        /// <remarks>
        /// Recovered only for routing, and only for the equality against text that selects the same
        /// documents either way. Routing narrows which partitions are visited and filters nothing; the
        /// predicate is still in the statement.
        /// </remarks>
        [TestMethod]
        public void APartitionKeyReachedThroughACastToTextIsRoutedOn()
        {
            ExtractPrefix(Eq(TextCast(Ref(1)), Str("bikes")), Container("/category"), out var values).Should().BeTrue();
            values.Should().Equal("bikes");
        }

        /// <remarks>
        /// Text a number renders as. A document storing the number 30 matches the predicate and lives in
        /// a different logical partition than one storing the string, so routing on it would skip the
        /// partition holding a matching document. Measured against the differential container, where
        /// exactly that document exists.
        /// </remarks>
        [TestMethod]
        public void APartitionKeyReachedThroughACastAgainstAmbiguousTextIsNotRoutedOn()
        {
            ExtractPrefix(Eq(TextCast(Ref(1)), Str("30")), Container("/category"), out _).Should().BeFalse();
        }

        /// <remarks>
        /// A cast to a number is not dropped anywhere, and least of all here.
        /// </remarks>
        [TestMethod]
        public void APartitionKeyReachedThroughANumericCastIsNotRoutedOn()
        {
            var cast = _rex.makeAbstractCast(_types.createSqlType(SqlTypeName.INTEGER), Ref(1), false);

            ExtractPrefix(Eq(cast, _rex.makeExactLiteral(new java.math.BigDecimal(30))), Container("/category"), out _).Should().BeFalse();
        }

        /// <summary>
        /// The operations that replace the predicate rather than route it keep the cast opaque.
        /// </summary>
        /// <remarks>
        /// A point read and a whole-partition delete apply no predicate of their own — the read returns
        /// whatever is at the key, the delete removes everything in the partition — so each needs its
        /// conjuncts accounted for in a stronger sense than routing does. Nothing about the cast form is
        /// known to fail there; it is simply not the place to find out, and a delete least of all.
        /// </remarks>
        [TestMethod]
        public void ACastToTextIsNotUsedWhereThePredicateWouldBeReplaced()
        {
            var condition = Eq(TextCast(Ref(1)), Str("bikes"));

            Extract(condition, Container("/category"), out _).Should().BeFalse();

            CosmosPartitionKeyExtractor.TryExtractWholePartition(condition, Fields, Container("/category"), "c", out _)
                .Should().BeFalse();

            CosmosPartitionKeyExtractor.TryExtractPointRead(
                And(condition, Eq(Ref(3), Str("x"))), Fields, Container("/category"), "c", out _, out _)
                .Should().BeFalse();
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

        // ── A set of point reads ──────────────────────────────────────────────────

        static bool ExtractSet(RexNode condition, CosmosContainerMetadata container, out IReadOnlyList<object?> values, out IReadOnlyList<string> ids) =>
            CosmosPartitionKeyExtractor.TryExtractPointReadSet(condition, Fields, container, "c", out values, out ids);

        /// <remarks>
        /// The shape <c>pk = 'x' AND id IN ('a', 'b')</c>, after <c>IN</c> has expanded to the
        /// disjunction of equalities the planner carries.
        /// </remarks>
        [TestMethod]
        public void ASetOfIdsWithACompleteKeyIsRecovered()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Or(Eq(Ref(3), Str("a")), Eq(Ref(3), Str("b"))));

            ExtractSet(condition, Container("/category"), out var values, out var ids).Should().BeTrue();
            values.Should().Equal("bikes");
            ids.Should().Equal("a", "b");
        }

        [TestMethod]
        public void DuplicateIdsCollapse()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Or(Eq(Ref(3), Str("a")), Eq(Ref(3), Str("a"))));

            ExtractSet(condition, Container("/category"), out _, out var ids).Should().BeTrue();
            ids.Should().Equal("a");
        }

        /// <remarks>
        /// The batch read applies no predicate, so a residual conjunct rules it out — the same
        /// blindness the single point read answers for.
        /// </remarks>
        [TestMethod]
        public void AResidualConjunctIsNotRecovered()
        {
            var condition = And(
                Eq(Ref(1), Str("bikes")),
                Or(Eq(Ref(3), Str("a")), Eq(Ref(3), Str("b"))),
                Eq(_rex.makeCall(SqlStdOperatorTable.ITEM, Ref(0), Str("price")), Str("5")));

            ExtractSet(condition, Container("/category"), out _, out _).Should().BeFalse();
        }

        [TestMethod]
        public void ABranchThatIsNotAnIdIsNotRecovered()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Or(Eq(Ref(3), Str("a")), Eq(Ref(1), Str("shoes"))));

            ExtractSet(condition, Container("/category"), out _, out _).Should().BeFalse();
        }

        [TestMethod]
        public void ASetWithoutACompleteKeyIsNotRecovered()
        {
            ExtractSet(Or(Eq(Ref(3), Str("a")), Eq(Ref(3), Str("b"))), Container("/category"), out _, out _).Should().BeFalse();
        }

        /// <remarks>
        /// A lone id equality is the single point read's question, asked first by the filter.
        /// </remarks>
        [TestMethod]
        public void ASingleIdEqualityIsLeftToThePointRead()
        {
            var condition = And(Eq(Ref(1), Str("bikes")), Eq(Ref(3), Str("a")));

            ExtractSet(condition, Container("/category"), out _, out _).Should().BeFalse();
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
