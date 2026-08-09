using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// Scalar function translation. The mappings and the two argument adjustments were measured
    /// against the service; see <c>DESIGN.md</c>.
    /// </summary>
    [TestClass]
    public class CosmosRexFunctionTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath> _fields;

        public CosmosRexFunctionTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath>
            {
                CosmosPath.Root("c").Property("s"),   // 0
                CosmosPath.Root("c").Property("n"),   // 1
                CosmosPath.Root("c").Property("a"),   // 2 — an array
                CosmosPath.Root("c").Property("m"),   // 3 — a multiset
                CosmosPath.Root("c").Property("o"),   // 4 — a map
            };
        }

        CosmosRexTranslator Translator() => new(_rex, _fields, new CosmosParameterList());

        RexNode Str() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 0);

        RexNode Num() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.DOUBLE), 1);

        RexNode Lit(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

        RexNode Int(int value) => _rex.makeExactLiteral(new java.math.BigDecimal(value));

        RexNode Array() => _rex.makeInputRef(_types.createArrayType(_types.createSqlType(SqlTypeName.VARCHAR), -1), 2);

        RexNode Multiset() => _rex.makeInputRef(_types.createMultisetType(_types.createSqlType(SqlTypeName.VARCHAR), -1), 3);

        RexNode Map() => _rex.makeInputRef(_types.createMapType(_types.createSqlType(SqlTypeName.VARCHAR), _types.createSqlType(SqlTypeName.ANY)), 4);

        RexNode Flag(SqlTrimFunction.Flag flag) => _rex.makeFlag(flag);

        string Translate(SqlOperator op, params RexNode[] operands) => Translator().Translate(_rex.makeCall(op, operands));

        bool CanTranslate(SqlOperator op, params RexNode[] operands) => Translator().TryTranslate(_rex.makeCall(op, operands), out _);

        // ── Direct mappings ───────────────────────────────────────────────────────

        [TestMethod]
        public void CaseFunctionsMapDirectly()
        {
            Translate(SqlStdOperatorTable.UPPER, Str()).Should().Be("UPPER(c.s)");
            Translate(SqlStdOperatorTable.LOWER, Str()).Should().Be("LOWER(c.s)");
        }

        [TestMethod]
        public void CharLengthMapsToLength()
        {
            Translate(SqlStdOperatorTable.CHAR_LENGTH, Str()).Should().Be("LENGTH(c.s)");
            Translate(SqlStdOperatorTable.CHARACTER_LENGTH, Str()).Should().Be("LENGTH(c.s)");
        }

        [TestMethod]
        public void ReplaceMapsDirectly()
        {
            Translate(SqlStdOperatorTable.REPLACE, Str(), Lit("a"), Lit("b")).Should().Be("REPLACE(c.s, @p0, @p1)");
        }

        [TestMethod]
        public void NumericFunctionsMapDirectly()
        {
            Translate(SqlStdOperatorTable.ABS, Num()).Should().Be("ABS(c.n)");
            Translate(SqlStdOperatorTable.EXP, Num()).Should().Be("EXP(c.n)");
            Translate(SqlStdOperatorTable.SQRT, Num()).Should().Be("SQRT(c.n)");
            Translate(SqlStdOperatorTable.SIGN, Num()).Should().Be("SIGN(c.n)");
            Translate(SqlStdOperatorTable.POWER, Num(), Int(2)).Should().Be("POWER(c.n, @p0)");
        }

        /// <remarks>
        /// Cosmos's natural logarithm is spelled LOG; its LOG10 is separate.
        /// </remarks>
        [TestMethod]
        public void NaturalLogMapsToLog()
        {
            Translate(SqlStdOperatorTable.LN, Num()).Should().Be("LOG(c.n)");
            Translate(SqlStdOperatorTable.LOG10, Num()).Should().Be("LOG10(c.n)");
        }

        [TestMethod]
        public void FloorMapsDirectly()
        {
            Translate(SqlStdOperatorTable.FLOOR, Num()).Should().Be("FLOOR(c.n)");
        }

        /// <remarks>
        /// Cosmos rejects CEIL; the function is named CEILING.
        /// </remarks>
        [TestMethod]
        public void CeilMapsToCeiling()
        {
            Translate(SqlStdOperatorTable.CEIL, Num()).Should().Be("CEILING(c.n)");
        }

        // ── Adjusted mappings ─────────────────────────────────────────────────────

        /// <remarks>
        /// SQL positions are one-based, Cosmos's are zero-based.
        /// </remarks>
        [TestMethod]
        public void SubstringShiftsTheStartPosition()
        {
            Translate(SqlStdOperatorTable.SUBSTRING, Str(), Int(1), Int(5))
                .Should().Be("SUBSTRING(c.s, (@p0 - 1), @p1)");
        }

        /// <remarks>
        /// Cosmos requires a length, so taking the rest of the string cannot be expressed.
        /// </remarks>
        [TestMethod]
        public void SubstringWithoutALengthIsDeclined()
        {
            CanTranslate(SqlStdOperatorTable.SUBSTRING, Str(), Int(2)).Should().BeFalse();
        }

        /// <remarks>
        /// INDEX_OF is zero-based and yields -1 when absent, so adding one reproduces SQL exactly
        /// on both counts. Note the operands swap: SQL is POSITION(needle IN haystack).
        /// </remarks>
        [TestMethod]
        public void PositionBecomesIndexOfPlusOne()
        {
            Translate(SqlStdOperatorTable.POSITION, Lit("World"), Str())
                .Should().Be("(INDEX_OF(c.s, @p0) + 1)");
        }

        // ── Trigonometry and the rest of the numeric set ──────────────────────────

        [TestMethod]
        public void TrigonometricFunctionsMapDirectly()
        {
            Translate(SqlStdOperatorTable.SIN, Num()).Should().Be("SIN(c.n)");
            Translate(SqlStdOperatorTable.COS, Num()).Should().Be("COS(c.n)");
            Translate(SqlStdOperatorTable.TAN, Num()).Should().Be("TAN(c.n)");
            Translate(SqlStdOperatorTable.COT, Num()).Should().Be("COT(c.n)");
            Translate(SqlStdOperatorTable.ASIN, Num()).Should().Be("ASIN(c.n)");
            Translate(SqlStdOperatorTable.ACOS, Num()).Should().Be("ACOS(c.n)");
            Translate(SqlStdOperatorTable.ATAN, Num()).Should().Be("ATAN(c.n)");
            Translate(SqlStdOperatorTable.DEGREES, Num()).Should().Be("DEGREES(c.n)");
            Translate(SqlStdOperatorTable.RADIANS, Num()).Should().Be("RADIANS(c.n)");
        }

        /// <remarks>
        /// The one name in the set that differs: Cosmos spells it ATN2, after T-SQL.
        /// </remarks>
        [TestMethod]
        public void Atan2IsSpelledAtn2()
        {
            Translate(SqlStdOperatorTable.ATAN2, Num(), Num()).Should().Be("ATN2(c.n, c.n)");
        }

        [TestMethod]
        public void PiIsWrittenAsANiladicCall()
        {
            Translate(SqlStdOperatorTable.PI).Should().Be("PI()");
        }

        [TestMethod]
        public void TruncateIsSpelledTrunc()
        {
            Translate(SqlStdOperatorTable.TRUNCATE, Num()).Should().Be("TRUNC(c.n)");
        }

        /// <remarks>
        /// Truncating to a number of decimal places is left out rather than guessed at; the arity of
        /// Cosmos's TRUNC has not been verified against the service.
        /// </remarks>
        [TestMethod]
        public void TruncateToDecimalPlacesIsDeclined()
        {
            CanTranslate(SqlStdOperatorTable.TRUNCATE, Num(), Int(2)).Should().BeFalse();
        }

        // ── Collections ───────────────────────────────────────────────────────────

        [TestMethod]
        public void CardinalityOverAnArrayCountsIt()
        {
            Translate(SqlStdOperatorTable.CARDINALITY, Array()).Should().Be("ARRAY_LENGTH(c.a)");
        }

        /// <remarks>
        /// SQL defines CARDINALITY over a map too, and ARRAY_LENGTH counts only an array. Emitting it
        /// for a map would report something meaningless rather than fail.
        /// </remarks>
        [TestMethod]
        public void CardinalityOverAMapIsDeclined()
        {
            CanTranslate(SqlStdOperatorTable.CARDINALITY, Map()).Should().BeFalse();
        }

        /// <remarks>
        /// The operands swap: SQL is value MEMBER OF multiset, and ARRAY_CONTAINS takes the array first.
        /// </remarks>
        [TestMethod]
        public void MemberOfBecomesArrayContainsWithTheOperandsSwapped()
        {
            Translate(SqlStdOperatorTable.MEMBER_OF, Lit("x"), Multiset()).Should().Be("ARRAY_CONTAINS(c.m, @p0)");
        }

        // ── TRIM ──────────────────────────────────────────────────────────────────

        [TestMethod]
        public void TrimSpecificationPicksTheCosmosFunction()
        {
            Translate(SqlStdOperatorTable.TRIM, Flag(SqlTrimFunction.Flag.BOTH), Lit(" "), Str()).Should().Be("TRIM(c.s)");
            Translate(SqlStdOperatorTable.TRIM, Flag(SqlTrimFunction.Flag.LEADING), Lit(" "), Str()).Should().Be("LTRIM(c.s)");
            Translate(SqlStdOperatorTable.TRIM, Flag(SqlTrimFunction.Flag.TRAILING), Lit(" "), Str()).Should().Be("RTRIM(c.s)");
        }

        /// <remarks>
        /// Cosmos's two-argument TRIM forms have not been verified, and emitting the one-argument form
        /// here would trim spaces where the query asked for something else.
        /// </remarks>
        [TestMethod]
        public void TrimOfANonSpaceCharacterIsDeclined()
        {
            CanTranslate(SqlStdOperatorTable.TRIM, Flag(SqlTrimFunction.Flag.BOTH), Lit("x"), Str()).Should().BeFalse();
        }

        // ── Declined ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void UnmappedFunctionIsDeclined()
        {
            CanTranslate(SqlStdOperatorTable.INITCAP, Str()).Should().BeFalse();
        }

        [TestMethod]
        public void WrongArityIsDeclined()
        {
            CanTranslate(SqlStdOperatorTable.REPLACE, Str(), Lit("a")).Should().BeFalse();
        }

    }

}
