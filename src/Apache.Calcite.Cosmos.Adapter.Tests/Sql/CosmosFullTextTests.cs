using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rex;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// Full text search, which is not SQL and so arrives through operators of this adapter's own.
    /// </summary>
    /// <remarks>
    /// The signatures are the service's, taken from the query language reference: the first argument of
    /// every function is a property path, and the <c>ALL</c> and <c>ANY</c> forms take one or more
    /// keywords after it.
    /// </remarks>
    [TestClass]
    public class CosmosFullTextTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;
        readonly List<CosmosPath?> _fields;

        public CosmosFullTextTests()
        {
            _rex = new RexBuilder(_types);
            _fields = new List<CosmosPath?>
            {
                CosmosPath.Root("c").Property("text"),   // 0
                null,                                    // 1 — a computed projection
            };
        }

        CosmosRexTranslator Translator() => new(_rex, _fields, new CosmosParameterList());

        RexNode Text() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 0);

        RexNode Computed() => _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 1);

        RexNode Keyword(string value) => _rex.makeLiteral(value, _types.createSqlType(SqlTypeName.VARCHAR, value.Length));

        string Translate(org.apache.calcite.sql.SqlOperator op, params RexNode[] operands) =>
            Translator().Translate(_rex.makeCall(op, operands));

        bool CanTranslate(org.apache.calcite.sql.SqlOperator op, params RexNode[] operands) =>
            Translator().TryTranslate(_rex.makeCall(op, operands), out _);

        [TestMethod]
        public void ContainsTakesAPathAndOneKeyword()
        {
            Translate(CosmosOperators.FullTextContains, Text(), Keyword("search phrase"))
                .Should().Be("FULLTEXTCONTAINS(c.text, @p0)");
        }

        [TestMethod]
        public void ContainsAllTakesEveryKeyword()
        {
            Translate(CosmosOperators.FullTextContainsAll, Text(), Keyword("one"), Keyword("two"), Keyword("three"))
                .Should().Be("FULLTEXTCONTAINSALL(c.text, @p0, @p1, @p2)");
        }

        [TestMethod]
        public void ContainsAnyTakesEveryKeyword()
        {
            Translate(CosmosOperators.FullTextContainsAny, Text(), Keyword("one"), Keyword("two"))
                .Should().Be("FULLTEXTCONTAINSANY(c.text, @p0, @p1)");
        }

        /// <remarks>
        /// Keywords bind as parameters like any other literal, so the statement text is independent of
        /// what is being searched for.
        /// </remarks>
        [TestMethod]
        public void KeywordsAreBoundRatherThanInlined()
        {
            var parameters = new CosmosParameterList();
            var translator = new CosmosRexTranslator(_rex, _fields, parameters);

            translator.Translate(_rex.makeCall(CosmosOperators.FullTextContains, Text(), Keyword("phrase")));

            parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be("phrase");
        }

        /// <remarks>
        /// The reference calls the first argument a property path, not an expression. A call over
        /// something with no path is refused rather than rendered into a statement the service rejects.
        /// </remarks>
        [TestMethod]
        public void APredicateOverAComputedColumnIsDeclined()
        {
            CanTranslate(CosmosOperators.FullTextContains, Computed(), Keyword("phrase")).Should().BeFalse();
        }

        [TestMethod]
        public void APredicateWithNoKeywordIsDeclined()
        {
            CanTranslate(CosmosOperators.FullTextContains, Text()).Should().BeFalse();
        }

        /// <remarks>
        /// The operator table exists so a query can name these at all; Calcite's standard table has
        /// nothing to resolve them to.
        /// </remarks>
        [TestMethod]
        public void TheOperatorTableCarriesEveryPredicate()
        {
            var names = new List<string>();
            var operators = CosmosOperators.Instance.getOperatorList();

            for (var i = 0; i < operators.size(); i++)
                names.Add(((org.apache.calcite.sql.SqlOperator)operators.get(i)).getName());

            names.Should().BeEquivalentTo([
                "FULLTEXTCONTAINS", "FULLTEXTCONTAINSALL", "FULLTEXTCONTAINSANY",
                "FULLTEXTSCORE", "RRF", "VECTORDISTANCE",
                "IS_DEFINED", "IS_ARRAY", "IS_BOOL", "IS_NULL",
                "IS_NUMBER", "IS_OBJECT", "IS_PRIMITIVE", "IS_STRING",
                // What has no SQL counterpart to map from: a regular expression dialect, and the
                // JSON conversions. The array functions are mapped instead — see the translator.
                "REGEXMATCH",
                "ToString", "StringToNumber", "StringToObject", "StringToArray", "StringToBoolean", "ObjectToArray",
            ]);
        }

        /// <remarks>
        /// The scoring functions are nameable — a query has to be able to write
        /// <c>ORDER BY FULLTEXTSCORE(…)</c> for <c>CosmosRankRule</c> to recognise the shape — but they
        /// are legal in that clause alone. The translator is what holds them to it, so naming one
        /// anywhere else declines rather than rendering a statement the service rejects.
        /// </remarks>
        [TestMethod]
        public void AScoringFunctionIsRefusedOutsideARankClause()
        {
            CanTranslate(CosmosOperators.FullTextScore, Text(), Keyword("steel")).Should().BeFalse();
            CanTranslate(CosmosOperators.Rrf, Text(), Keyword("steel")).Should().BeFalse();
        }

        /// <remarks>
        /// And renders in one. This is the only entry point that permits it.
        /// </remarks>
        [TestMethod]
        public void AScoringFunctionRendersInARankClause()
        {
            Translator().TranslateRank(_rex.makeCall(CosmosOperators.FullTextScore, Text(), Keyword("steel")))
                .Should().Be("FULLTEXTSCORE(c.text, @p0)");
        }

        /// <remarks>
        /// An ordinary expression is not something to rank by.
        /// </remarks>
        [TestMethod]
        public void ARankClauseTakesOnlyAScoringFunction()
        {
            var act = () => Translator().TranslateRank(Text());
            act.Should().Throw<CosmosTranslationException>();
        }


        // ── Type tests ──────────────────────────────────────────────

        /// <remarks>
        /// A container has no row schema, so what a property holds is a question about the document.
        /// These are the functions that ask, and every one was accepted by the emulator.
        /// </remarks>
        [TestMethod]
        public void TypeTestsRenderUnderTheirOwnNames()
        {
            Translate(CosmosOperators.IsDefined, Text()).Should().Be("IS_DEFINED(c.text)");
            Translate(CosmosOperators.IsArray, Text()).Should().Be("IS_ARRAY(c.text)");
            Translate(CosmosOperators.IsBool, Text()).Should().Be("IS_BOOL(c.text)");
            Translate(CosmosOperators.IsNull, Text()).Should().Be("IS_NULL(c.text)");
            Translate(CosmosOperators.IsNumber, Text()).Should().Be("IS_NUMBER(c.text)");
            Translate(CosmosOperators.IsObject, Text()).Should().Be("IS_OBJECT(c.text)");
            Translate(CosmosOperators.IsPrimitive, Text()).Should().Be("IS_PRIMITIVE(c.text)");
            Translate(CosmosOperators.IsString, Text()).Should().Be("IS_STRING(c.text)");
        }

        /// <remarks>
        /// Unlike a full text predicate, the argument is an ordinary expression: asking the type of a
        /// computed value is meaningful, so nothing requires it to be a path.
        /// </remarks>
        [TestMethod]
        public void ATypeTestOverAComputedColumnIsStillDeclined()
        {
            // Not because the function objects, but because the ordinal has no path to render at all.
            CanTranslate(CosmosOperators.IsDefined, Computed()).Should().BeFalse();
        }

    }

}