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
            };
        }

        CosmosRexTranslator Translator() => new(_rex, _fields, new CosmosParameterList());

        RexNode Str() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 0);

        RexNode Num() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.DOUBLE), 1);

        RexNode Lit(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

        RexNode Int(int value) => _rex.makeExactLiteral(new java.math.BigDecimal(value));

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

        // ── Declined ──────────────────────────────────────────────────────────────

        /// <remarks>
        /// Cosmos TRIM only strips whitespace, where SQL trims an arbitrary character set from a
        /// chosen end.
        /// </remarks>
        [TestMethod]
        public void TrimIsDeclined()
        {
            CanTranslate(SqlStdOperatorTable.TRIM, Lit(" "), Lit(" "), Str()).Should().BeFalse();
        }

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
