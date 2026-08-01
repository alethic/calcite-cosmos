using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Drives the Volcano planner over real SQL with the Cosmos rule set registered, asserting that
    /// the planner actually selects the Cosmos nodes.
    /// </summary>
    /// <remarks>
    /// The rule predicates and each node's rendering are covered elsewhere. What is checked here is
    /// the step between them: that the planner reaches a plan wholly in the Cosmos convention, and
    /// that the plan renders to the expected statement.
    /// </remarks>
    [TestClass]
    public class CosmosPlannerTests
    {

        static readonly CosmosContainerMetadata Products = new(
            "products",
            new[] { "/category" },
            new[]
            {
                new CosmosCompositeIndex(new[]
                {
                    new CosmosCompositeIndexPath("/id", false),
                    new CosmosCompositeIndexPath("/_ts", false),
                }),
            });

        CosmosTable _table = null!;

        [TestInitialize]
        public void Initialize()
        {
            _table = new CosmosTable(Products);
        }

        RelNode PlanLogical(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var validator = SqlValidatorUtil.newValidator(
                SqlStdOperatorTable.instance(), catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).rel;
        }

        /// <summary>
        /// Plans a statement and asks the planner for the best plan wholly in the Cosmos convention.
        /// </summary>
        RelNode PlanToCosmos(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(_table.Convention).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// Renders a planned tree to the statement it would execute.
        /// </summary>
        string Render(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), Products);
            implementor.Visit(rel);
            return implementor.Build().Sql;
        }

        static string Plan(RelNode rel) => RelOptUtil.toString(rel).Trim().Replace("\r\n", "\n");

        // ── The planner selects Cosmos nodes ──────────────────────────────────────

        [TestMethod]
        public void ScanAlonePlansInTheCosmosConvention()
        {
            var best = PlanToCosmos("SELECT * FROM products");

            Plan(best).Should().Contain("CosmosTableScan");
            best.getConvention().Should().BeSameAs(_table.Convention);
        }

        [TestMethod]
        public void FilterIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'");

            Plan(best).Should().Contain("CosmosFilter");
            Render(best).Should().Contain("WHERE (c.id = @p0)");
        }

        [TestMethod]
        public void ProjectIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c");

            Plan(best).Should().Contain("CosmosProject");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c");
        }

        [TestMethod]
        public void FilterAndProjectPlanTogether()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE c.\"category\" = 'bikes'");
            var sql = Render(best);

            sql.Should().StartWith("SELECT VALUE { \"id\": c.id } FROM products c WHERE ");
            sql.Should().Contain("c.category = @p0");
        }

        /// <remarks>
        /// The container declares a composite index over (/id, /_ts), so this multi-key sort is
        /// legal and the rule may fire.
        /// </remarks>
        [TestMethod]
        public void SortIsSelectedWhenTheCompositeIndexPermitsIt()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c ORDER BY c.\"id\", c.\"_ts\"");

            Plan(best).Should().Contain("CosmosSort");
            Render(best).Should().Contain("ORDER BY c.id ASC, c._ts ASC");
        }

        /// <summary>
        /// The end of the chain: an array traversal planned from SQL, selected by the planner, and
        /// rendered to Cosmos SQL.
        /// </summary>
        [TestMethod]
        public void UnnestIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c, UNNEST(c.\"_MAP\"['tags']) AS t");

            Plan(best).Should().Contain("CosmosUnnest");
            Render(best).Should().Be("SELECT VALUE { \"id\": c.id } FROM products c JOIN t0 IN c.tags");
        }

        // ── Aggregation ───────────────────────────────────────────────────────────

        [TestMethod]
        public void GroupByWithCountIsSelectedByThePlanner()
        {
            var best = PlanToCosmos("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY c.\"category\"");

            Plan(best).Should().Contain("CosmosAggregate");
            Render(best).Should().Contain("GROUP BY c.category");
            Render(best).Should().Contain("COUNT(1)");
        }

        /// <remarks>
        /// <c>_ts</c> is service-guaranteed and therefore non-nullable, so Cosmos and SQL agree on
        /// the aggregate's value.
        /// </remarks>
        [TestMethod]
        public void AggregateOverANonNullableColumnIsSelected()
        {
            var best = PlanToCosmos("SELECT c.\"category\", MAX(c.\"_ts\") FROM products AS c GROUP BY c.\"category\"");

            Plan(best).Should().Contain("CosmosAggregate");
            Render(best).Should().Contain("MAX(c._ts)");
        }

        [TestMethod]
        public void GroupByRendersTheWholeStatement()
        {
            var sql = Render(PlanToCosmos("SELECT c.\"category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"category\""));

            // Flat rather than an object constructor: Cosmos rejects an aggregate inside one.
            sql.Should().Be("SELECT c.category AS \"category\", COUNT(1) AS \"n\" FROM products c GROUP BY c.category");
        }

        // ── The planner declines rather than guessing ─────────────────────────────

        /// <remarks>
        /// Measured on the emulator, Cosmos <c>COUNT(x)</c> counts a JSON null where SQL excludes
        /// it, so the two disagree on any nullable column.
        /// </remarks>
        [TestMethod]
        public void CountOfANullableColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT COUNT(c.\"category\") FROM products AS c");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// <c>SUM</c> over a set containing a JSON null returns undefined rather than ignoring it.
        /// </remarks>
        [TestMethod]
        public void SumOfANullableColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT SUM(c.\"category\") FROM products AS c");

            act.Should().Throw<Exception>();
        }

        [TestMethod]
        public void DistinctAggregateIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT COUNT(DISTINCT c.\"id\") FROM products AS c");

            act.Should().Throw<Exception>();
        }


        /// <remarks>
        /// UPPER has no Cosmos equivalent, so no rule converts the filter. With only Cosmos rules
        /// registered the planner cannot reach a plan at all, which is the correct outcome: in a
        /// real planning context the operator is left to Calcite's own runtime.
        /// </remarks>
        [TestMethod]
        public void UntranslatableFilterIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT * FROM products AS c WHERE INITCAP(c.\"id\") = 'X'");

            act.Should().Throw<Exception>();
        }

        /// <remarks>
        /// A multi-key sort with no matching composite index is rejected by the service, so the
        /// rule must not fire.
        /// </remarks>
        [TestMethod]
        public void SortWithoutAMatchingCompositeIndexIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT * FROM products AS c ORDER BY c.\"id\", c.\"category\"");

            act.Should().Throw<Exception>();
        }

    }

}
