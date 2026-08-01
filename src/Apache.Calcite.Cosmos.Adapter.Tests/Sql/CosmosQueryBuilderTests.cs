using System;

using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    [TestClass]
    public class CosmosQueryBuilderTests
    {

        static CosmosQueryBuilder Builder() => new("products", "c");

        [TestMethod]
        public void NoProjectionReturnsTheDocument()
        {
            Builder().Build().Should().Be("SELECT VALUE c FROM products c");
        }

        [TestMethod]
        public void ValueProjectionIsUnwrapped()
        {
            var b = Builder();
            b.SelectValue("c.name");
            b.Build().Should().Be("SELECT VALUE c.name FROM products c");
        }

        [TestMethod]
        public void PropertyProjectionBecomesAnObjectConstructor()
        {
            var b = Builder();
            b.SelectProperty("id", "c.id");
            b.SelectProperty("city", "c.address.city");
            b.Build().Should().Be("SELECT VALUE { \"id\": c.id, \"city\": c.address.city } FROM products c");
        }

        [TestMethod]
        public void ProjectionAliasesAreQuoted()
        {
            var b = Builder();
            b.SelectProperty("odd name", "c.x");
            b.Build().Should().Be("SELECT VALUE { \"odd name\": c.x } FROM products c");
        }

        [TestMethod]
        public void DistinctAndTopArePlacedBeforeTheProjection()
        {
            var b = Builder();
            b.Distinct = true;
            b.Top = 5;
            b.SelectValue("c.category");
            b.Build().Should().Be("SELECT DISTINCT TOP 5 VALUE c.category FROM products c");
        }

        [TestMethod]
        public void WhereIsEmitted()
        {
            var b = Builder();
            b.Where = "c.price > @p0";
            b.Build().Should().Be("SELECT VALUE c FROM products c WHERE c.price > @p0");
        }

        [TestMethod]
        public void UnnestBecomesJoinIn()
        {
            var b = Builder();
            b.AddUnnest("t", "c.tags");
            b.SelectValue("t");
            b.Build().Should().Be("SELECT VALUE t FROM products c JOIN t IN c.tags");
        }

        [TestMethod]
        public void MultipleUnnestsAreOrdered()
        {
            var b = Builder();
            b.AddUnnest("t", "c.tags");
            b.AddUnnest("s", "c.sizes");
            b.Build().Should().Be("SELECT VALUE c FROM products c JOIN t IN c.tags JOIN s IN c.sizes");
        }

        [TestMethod]
        public void GroupByIsEmitted()
        {
            var b = Builder();
            b.SelectProperty("category", "c.category");
            b.AddGroupBy("c.category");
            b.Build().Should().Be("SELECT VALUE { \"category\": c.category } FROM products c GROUP BY c.category");
        }

        [TestMethod]
        public void OrderByCarriesDirection()
        {
            var b = Builder();
            b.AddOrderBy("c.price", descending: false);
            b.AddOrderBy("c.name", descending: true);
            b.Build().Should().Be("SELECT VALUE c FROM products c ORDER BY c.price ASC, c.name DESC");
        }

        [TestMethod]
        public void OffsetAndLimitAreEmittedTogether()
        {
            var b = Builder();
            b.Offset = 10;
            b.Fetch = 20;
            b.Build().Should().Be("SELECT VALUE c FROM products c OFFSET 10 LIMIT 20");
        }

        [TestMethod]
        public void FetchWithoutOffsetStillEmitsOffsetZero()
        {
            var b = Builder();
            b.Fetch = 20;
            b.Build().Should().Be("SELECT VALUE c FROM products c OFFSET 0 LIMIT 20");
        }

        [TestMethod]
        public void OffsetWithoutFetchStillEmitsLimit()
        {
            var b = Builder();
            b.Offset = 10;
            b.Build().Should().EndWith("OFFSET 10 LIMIT 2147483647");
        }

        [TestMethod]
        public void ClausesAppearInOrder()
        {
            var b = Builder();
            b.SelectProperty("n", "c.name");
            b.AddUnnest("t", "c.tags");
            b.Where = "t.key = @p0";
            b.AddOrderBy("c.name", descending: false);
            b.Offset = 5;
            b.Fetch = 10;

            b.Build().Should().Be(
                "SELECT VALUE { \"n\": c.name } FROM products c JOIN t IN c.tags " +
                "WHERE t.key = @p0 ORDER BY c.name ASC OFFSET 5 LIMIT 10");
        }

        // The language constraints. These are the reason the builder exists.

        [TestMethod]
        public void GroupByWithOrderByIsRejected()
        {
            var b = Builder();
            b.AddGroupBy("c.category");
            b.AddOrderBy("c.category", descending: false);

            var act = () => b.Build();
            act.Should().Throw<InvalidOperationException>().WithMessage("*GROUP BY and ORDER BY*");
        }

        [TestMethod]
        public void TopWithOffsetLimitIsRejected()
        {
            var b = Builder();
            b.Top = 5;
            b.Fetch = 10;

            var act = () => b.Build();
            act.Should().Throw<InvalidOperationException>().WithMessage("*TOP*OFFSET*");
        }

        [TestMethod]
        public void ValueProjectionCannotBeMixedWithProperties()
        {
            var b = Builder();
            b.SelectValue("c.name");

            var act = () => b.SelectProperty("x", "c.x");
            act.Should().Throw<InvalidOperationException>();
        }

        [TestMethod]
        public void PropertyProjectionCannotBeMixedWithValue()
        {
            var b = Builder();
            b.SelectProperty("x", "c.x");

            var act = () => b.SelectValue("c.name");
            act.Should().Throw<InvalidOperationException>();
        }

        [TestMethod]
        public void NegativeFetchIsRejected()
        {
            var b = Builder();
            b.Fetch = -1;

            var act = () => b.Build();
            act.Should().Throw<InvalidOperationException>();
        }

    }

}
