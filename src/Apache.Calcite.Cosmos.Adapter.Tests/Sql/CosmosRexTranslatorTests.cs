using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    [TestClass]
    public class CosmosRexTranslatorTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath> _fields;
        CosmosParameterList _parameters = null!;

        public CosmosRexTranslatorTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath>
            {
                CosmosPath.Root("c").Property("name"),     // 0
                CosmosPath.Root("c").Property("price"),    // 1
                CosmosPath.Root("c"),                      // 2 — the whole document
            };
        }

        CosmosRexTranslator Translator()
        {
            _parameters = new CosmosParameterList();
            return new CosmosRexTranslator(_rex, _fields, _parameters);
        }

        RexNode Ref(int index, SqlTypeName type) => _rex.makeInputRef(_types.createSqlType(type), index);

        RexNode Str(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, System.Math.Max(1, value.Length)));

        RexNode Num(int value) => _rex.makeExactLiteral(new java.math.BigDecimal(value));

        RexNode Call(SqlOperator op, params RexNode[] operands) => _rex.makeCall(op, operands);

        string Translate(RexNode node) => Translator().Translate(node);

        static bool CanTranslate(CosmosRexTranslator translator, RexNode node) => translator.TryTranslate(node, out _);

        // ── Field references ──────────────────────────────────────────────────────

        [TestMethod]
        public void InputRefResolvesToItsPath()
        {
            Translate(Ref(0, SqlTypeName.VARCHAR)).Should().Be("c.name");
        }

        [TestMethod]
        public void UnboundOrdinalIsDeclined()
        {
            CanTranslate(Translator(), Ref(99, SqlTypeName.VARCHAR)).Should().BeFalse();
        }

        // ── Literals ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void LiteralsAreBoundNotInlined()
        {
            var t = Translator();
            t.Translate(Str("abc")).Should().Be("@p0");
            _parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be("abc");
        }

        [TestMethod]
        public void IntegerLiteralBindsAsLong()
        {
            var t = Translator();
            t.Translate(Num(42));
            _parameters.Parameters[0].Value.Should().Be(42L);
        }

        [TestMethod]
        public void BooleanLiteralBinds()
        {
            var t = Translator();
            t.Translate(_rex.makeLiteral(true));
            _parameters.Parameters[0].Value.Should().Be(true);
        }

        [TestMethod]
        public void ApproximateLiteralBindsAsDouble()
        {
            var t = Translator();
            t.Translate(_rex.makeApproxLiteral(new java.math.BigDecimal("1.5")));
            _parameters.Parameters[0].Value.Should().Be(1.5d);
        }

        /// <remarks>
        /// Cosmos has no date type and nothing declares whether a container stores dates as ISO
        /// strings or epoch numbers, so a temporal literal must not be guessed.
        /// </remarks>
        [TestMethod]
        public void TemporalLiteralIsDeclined()
        {
            CanTranslate(Translator(), _rex.makeDateLiteral(new DateString("2020-01-01"))).Should().BeFalse();
        }

        // ── Operators ─────────────────────────────────────────────────────────────

        [TestMethod]
        public void ComparisonRenders()
        {
            Translate(Call(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("abc")))
                .Should().Be("(c.name = @p0)");
        }

        [TestMethod]
        public void NotEqualsUsesCosmosSpelling()
        {
            Translate(Call(SqlStdOperatorTable.NOT_EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("abc")))
                .Should().Be("(c.name != @p0)");
        }

        [TestMethod]
        public void ConjunctionChainsWithoutNesting()
        {
            var a = Call(SqlStdOperatorTable.GREATER_THAN, Ref(1, SqlTypeName.INTEGER), Num(5));
            var b = Call(SqlStdOperatorTable.LESS_THAN, Ref(1, SqlTypeName.INTEGER), Num(10));

            Translate(Call(SqlStdOperatorTable.AND, a, b))
                .Should().Be("((c.price > @p0) AND (c.price < @p1))");
        }

        [TestMethod]
        public void ArithmeticRenders()
        {
            Translate(Call(SqlStdOperatorTable.MULTIPLY, Ref(1, SqlTypeName.INTEGER), Num(2)))
                .Should().Be("(c.price * @p0)");
        }

        [TestMethod]
        public void NotRenders()
        {
            var inner = Call(SqlStdOperatorTable.EQUALS, Ref(0, SqlTypeName.VARCHAR), Str("x"));
            Translate(Call(SqlStdOperatorTable.NOT, inner)).Should().Be("(NOT (c.name = @p0))");
        }

        /// <remarks>
        /// SQL has one null; Cosmos distinguishes absent from present-and-null. Both states must
        /// be tested or a filter would miss documents that simply lack the property.
        /// </remarks>
        [TestMethod]
        public void IsNullTestsBothUndefinedAndNull()
        {
            Translate(Call(SqlStdOperatorTable.IS_NULL, Ref(0, SqlTypeName.VARCHAR)))
                .Should().Be("(NOT IS_DEFINED(c.name) OR IS_NULL(c.name))");
        }

        [TestMethod]
        public void IsNotNullTestsBothUndefinedAndNull()
        {
            Translate(Call(SqlStdOperatorTable.IS_NOT_NULL, Ref(0, SqlTypeName.VARCHAR)))
                .Should().Be("(IS_DEFINED(c.name) AND NOT IS_NULL(c.name))");
        }

        [TestMethod]
        public void LikeRenders()
        {
            Translate(Call(SqlStdOperatorTable.LIKE, Ref(0, SqlTypeName.VARCHAR), Str("%bike%")))
                .Should().Be("(c.name LIKE @p0)");
        }

        /// <remarks>
        /// A single trailing <c>%</c> is a prefix match, which the index serves as
        /// <c>STARTSWITH</c> where <c>LIKE</c> is a scan. The bound parameter is the prefix, not
        /// the pattern.
        /// </remarks>
        [TestMethod]
        public void PrefixLikeRendersAsStartsWith()
        {
            var t = Translator();
            t.Translate(Call(SqlStdOperatorTable.LIKE, Ref(0, SqlTypeName.VARCHAR), Str("bike%")))
                .Should().Be("STARTSWITH(c.name, @p0)");

            _parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be("bike");
        }

        [TestMethod]
        public void LikeWithAnInnerWildcardStaysLike()
        {
            Translate(Call(SqlStdOperatorTable.LIKE, Ref(0, SqlTypeName.VARCHAR), Str("bi%ke%")))
                .Should().Be("(c.name LIKE @p0)");

            Translate(Call(SqlStdOperatorTable.LIKE, Ref(0, SqlTypeName.VARCHAR), Str("bi_e%")))
                .Should().Be("(c.name LIKE @p0)");
        }

        /// <remarks>
        /// Cosmos <c>LIKE</c> reads <c>[…]</c> as a character range; SQL matches the brackets
        /// literally. Pushing such a pattern would change which rows match, so it is declined.
        /// </remarks>
        [TestMethod]
        public void LikeWithABracketIsDeclined()
        {
            CanTranslate(Translator(), Call(SqlStdOperatorTable.LIKE, Ref(0, SqlTypeName.VARCHAR), Str("[b]ike%")))
                .Should().BeFalse();
        }

        /// <remarks>
        /// A computed pattern cannot be checked for the bracket divergence, so it is declined whole
        /// rather than pushed on the hope that no value contains one.
        /// </remarks>
        [TestMethod]
        public void LikeWithAComputedPatternIsDeclined()
        {
            CanTranslate(Translator(), Call(SqlStdOperatorTable.LIKE, Ref(0, SqlTypeName.VARCHAR), Ref(0, SqlTypeName.VARCHAR)))
                .Should().BeFalse();
        }

        // ── Scalar functions ──────────────────────────────────────────────────────

        /// <remarks>
        /// Spelled alike at both ends, and meaning alike: the count is a character count and both
        /// clamp rather than fail where it exceeds the string.
        /// </remarks>
        [TestMethod]
        public void LeftAndRightRenderUnchanged()
        {
            Translate(Call(SqlLibraryOperators.LEFT, Ref(0, SqlTypeName.VARCHAR), Num(3)))
                .Should().Be("LEFT(c.name, @p0)");

            Translate(Call(SqlLibraryOperators.RIGHT, Ref(0, SqlTypeName.VARCHAR), Num(3)))
                .Should().Be("RIGHT(c.name, @p0)");
        }

        [TestMethod]
        public void ReverseRendersUnchanged()
        {
            Translate(Call(SqlLibraryOperators.REVERSE, Ref(0, SqlTypeName.VARCHAR)))
                .Should().Be("REVERSE(c.name)");
        }

        /// <remarks>
        /// The one string function whose name differs: SQL repeats with <c>REPEAT</c> and Cosmos
        /// with <c>REPLICATE</c>, same arguments in the same order.
        /// </remarks>
        [TestMethod]
        public void RepeatBecomesReplicate()
        {
            Translate(Call(SqlLibraryOperators.REPEAT, Ref(0, SqlTypeName.VARCHAR), Num(2)))
                .Should().Be("REPLICATE(c.name, @p0)");
        }

        /// <remarks>
        /// The index origin is a translation detail, not a difference in meaning — Calcite counts
        /// from one and Cosmos from zero, so the start is shifted exactly as <c>SUBSTRING</c>'s is.
        /// </remarks>
        [TestMethod]
        public void ArraySliceShiftsTheStartToCosmosOrigin()
        {
            Translate(Call(SqlLibraryOperators.ARRAY_SLICE, Ref(2, SqlTypeName.ANY), Num(1), Num(2)))
                .Should().Be("ARRAY_SLICE(c, (@p0 - 1), @p1)");
        }

        [TestMethod]
        public void TheSetFunctionsMapFromTheirSqlCounterparts()
        {
            Translate(Call(SqlLibraryOperators.ARRAY_CONCAT, Ref(2, SqlTypeName.ANY), Ref(2, SqlTypeName.ANY)))
                .Should().Be("ARRAY_CONCAT(c, c)");

            Translate(Call(SqlLibraryOperators.ARRAY_INTERSECT, Ref(2, SqlTypeName.ANY), Ref(2, SqlTypeName.ANY)))
                .Should().Be("SETINTERSECT(c, c)");

            Translate(Call(SqlLibraryOperators.ARRAY_UNION, Ref(2, SqlTypeName.ANY), Ref(2, SqlTypeName.ANY)))
                .Should().Be("SETUNION(c, c)");
        }

        /// <remarks>
        /// Under its own name deliberately: regular expression dialects differ in ways a query
        /// cannot see, and the <c>LIKE</c> measurement is the argument for not quietly equating two.
        /// </remarks>
        [TestMethod]
        public void RegexMatchRendersUnderItsOwnName()
        {
            Translate(Call(CosmosOperators.RegexMatch, Ref(0, SqlTypeName.VARCHAR), Str("^Tr")))
                .Should().Be("REGEXMATCH(c.name, @p0)");

            Translate(Call(CosmosOperators.RegexMatch, Ref(0, SqlTypeName.VARCHAR), Str("^tr"), Str("i")))
                .Should().Be("REGEXMATCH(c.name, @p0, @p1)");
        }

        [TestMethod]
        public void TheJsonConversionsRender()
        {
            Translate(Call(CosmosOperators.ToStringFunction, Ref(1, SqlTypeName.ANY)))
                .Should().Be("ToString(c.price)");

            Translate(Call(CosmosOperators.StringToNumber, Str("42")))
                .Should().Be("StringToNumber(@p0)");

            Translate(Call(CosmosOperators.ObjectToArray, Ref(2, SqlTypeName.ANY)))
                .Should().Be("ObjectToArray(c)");
        }

        [TestMethod]
        public void CaseBecomesNestedTernary()
        {
            var cond = Call(SqlStdOperatorTable.GREATER_THAN, Ref(1, SqlTypeName.INTEGER), Num(5));
            Translate(Call(SqlStdOperatorTable.CASE, cond, Num(1), Num(0)))
                .Should().Be("((c.price > @p0) ? @p1 : @p2)");
        }

        // ── ITEM, the operator the map row model depends on ───────────────────────

        [TestMethod]
        public void ItemBecomesAPathExtension()
        {
            Translate(Call(SqlStdOperatorTable.ITEM, Ref(2, SqlTypeName.ANY), Str("city")))
                .Should().Be("c.city");
        }

        [TestMethod]
        public void NestedItemChainsThePath()
        {
            var inner = Call(SqlStdOperatorTable.ITEM, Ref(2, SqlTypeName.ANY), Str("address"));
            Translate(Call(SqlStdOperatorTable.ITEM, inner, Str("city")))
                .Should().Be("c.address.city");
        }

        [TestMethod]
        public void ItemQuotesAwkwardPropertyNames()
        {
            Translate(Call(SqlStdOperatorTable.ITEM, Ref(2, SqlTypeName.ANY), Str("odd name")))
                .Should().Be("c[\"odd name\"]");
        }

        [TestMethod]
        public void ItemWithIntegerAccessorIndexesTheArray()
        {
            Translate(Call(SqlStdOperatorTable.ITEM, Ref(2, SqlTypeName.ANY), Num(0)))
                .Should().Be("c[0]");
        }

        // The translator also refuses ITEM whose base is not a path, since appending a segment
        // would emit nonsense such as "@p0.name". That guard is not exercised here: Calcite's own
        // SqlItemOperator.inferReturnType rejects such a call at construction, so the node cannot
        // be built to test against. The guard remains as defence against operand shapes Calcite
        // does admit.

        [TestMethod]
        public void ItemWithNonConstantAccessorIsDeclined()
        {
            var node = Call(SqlStdOperatorTable.ITEM, Ref(2, SqlTypeName.ANY), Ref(0, SqlTypeName.VARCHAR));
            CanTranslate(Translator(), node).Should().BeFalse();
        }

        // ── Refusal ───────────────────────────────────────────────────────────────

        /// <remarks>
        /// Cosmos SUBSTRING is zero-based where SQL is one-based. Declining is correct until the
        /// offset adjustment is implemented and tested; approximating it would silently return
        /// wrong values.
        /// </remarks>
        [TestMethod]
        public void UnsupportedFunctionIsDeclined()
        {
            CanTranslate(Translator(), Call(SqlStdOperatorTable.INITCAP, Ref(0, SqlTypeName.VARCHAR))).Should().BeFalse();
        }

        [TestMethod]
        public void DecliningLeavesNoPartialExpression()
        {
            var t = Translator();
            t.TryTranslate(Call(SqlStdOperatorTable.INITCAP, Ref(0, SqlTypeName.VARCHAR)), out var expression).Should().BeFalse();
            expression.Should().BeNull();
        }

        // ── SEARCH expansion ──────────────────────────────────────────────────────

        /// <remarks>
        /// Calcite rewrites comparison chains and IN lists into SEARCH over a Sarg. Without
        /// expanding them first, ordinary range and set predicates would never push down.
        /// </remarks>
        [TestMethod]
        public void SearchIsExpandedRatherThanDeclined()
        {
            var node = _rex.makeIn(Ref(1, SqlTypeName.INTEGER), java.util.Arrays.asList(Num(1), Num(2), Num(3)));

            var t = Translator();
            t.TryTranslate(node, out var expression).Should().BeTrue();
            expression.Should().Contain("c.price");
        }

    }

}
