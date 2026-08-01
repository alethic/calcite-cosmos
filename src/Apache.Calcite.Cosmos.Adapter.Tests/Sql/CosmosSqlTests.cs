using System;
using System.Text;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    [TestClass]
    public class CosmosSqlTests
    {

        static string Property(string name)
        {
            var builder = new StringBuilder();
            CosmosSql.WritePropertyAccess(builder, name);
            return builder.ToString();
        }

        static string Literal(object? value)
        {
            var builder = new StringBuilder();
            CosmosSql.WriteLiteral(builder, value);
            return builder.ToString();
        }

        [DataTestMethod]
        [DataRow("name")]
        [DataRow("_ts")]
        [DataRow("_etag")]
        [DataRow("a1")]
        [DataRow("A_B_9")]
        public void BareIdentifiersAreAccepted(string name)
        {
            CosmosSql.IsBareIdentifier(name).Should().BeTrue();
        }

        [DataTestMethod]
        [DataRow("")]
        [DataRow("odd name")]
        [DataRow("0abc")]
        [DataRow("has-dash")]
        [DataRow("has.dot")]
        [DataRow("café")]
        public void NonBareIdentifiersAreRejected(string name)
        {
            CosmosSql.IsBareIdentifier(name).Should().BeFalse();
        }

        [DataTestMethod]
        [DataRow("TOP")]
        [DataRow("top")]
        [DataRow("VALUE")]
        [DataRow("order")]
        public void ReservedWordsAreNotBareIdentifiers(string name)
        {
            CosmosSql.IsBareIdentifier(name).Should().BeFalse();
        }

        [TestMethod]
        public void BarePropertyUsesDotNotation()
        {
            Property("name").Should().Be(".name");
        }

        [TestMethod]
        public void NonBarePropertyUsesBracketNotation()
        {
            Property("odd name").Should().Be("[\"odd name\"]");
        }

        [TestMethod]
        public void ReservedPropertyUsesBracketNotation()
        {
            Property("VALUE").Should().Be("[\"VALUE\"]");
        }

        [TestMethod]
        public void StringLiteralEscapesQuotesAndBackslashes()
        {
            Literal("say \"hi\"\\").Should().Be("\"say \\\"hi\\\"\\\\\"");
        }

        [TestMethod]
        public void StringLiteralEscapesControlCharacters()
        {
            Literal("a\r\n\tb\u0001").Should().Be("\"a\\r\\n\\tb\\u0001\"");
        }

        [TestMethod]
        public void PropertyNameWithQuoteIsEscapedInBrackets()
        {
            Property("a\"b").Should().Be("[\"a\\\"b\"]");
        }

        [TestMethod]
        public void NullRendersAsJsonNull()
        {
            Literal(null).Should().Be("null");
        }

        [DataTestMethod]
        [DataRow(true, "true")]
        [DataRow(false, "false")]
        public void BooleanRendersAsJsonBoolean(bool value, string expected)
        {
            Literal(value).Should().Be(expected);
        }

        [TestMethod]
        public void IntegersRenderWithoutSeparators()
        {
            Literal(1234567).Should().Be("1234567");
            Literal(-42L).Should().Be("-42");
        }

        [TestMethod]
        public void DecimalRendersInvariant()
        {
            Literal(1.5m).Should().Be("1.5");
        }

        [TestMethod]
        public void DoubleRoundTrips()
        {
            Literal(0.1d).Should().Be("0.1");
            Literal(1e20d).Should().Be("1E+20");
        }

        [TestMethod]
        public void NonFiniteDoubleIsRejected()
        {
            var act = () => Literal(double.NaN);
            act.Should().Throw<NotSupportedException>();
        }

        [TestMethod]
        public void UnsupportedTypeIsRejected()
        {
            var act = () => Literal(new object());
            act.Should().Throw<NotSupportedException>();
        }

        [TestMethod]
        public void NegativeIndexIsRejected()
        {
            var act = () => CosmosSql.WriteIndexAccess(new StringBuilder(), -1);
            act.Should().Throw<ArgumentOutOfRangeException>();
        }

    }

}
