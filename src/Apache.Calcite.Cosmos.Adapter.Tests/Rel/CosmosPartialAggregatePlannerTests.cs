using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;

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
    /// Covers aggregates that cannot be pushed whole but are pushed in part, with the plan finishing
    /// the rest.
    /// </summary>
    /// <remarks>
    /// <c>COUNT(DISTINCT x)</c> is the case that exists today: Calcite's own
    /// <c>AGGREGATE_EXPAND_DISTINCT_AGGREGATES</c> rewrites it into an aggregate over an aggregate,
    /// the inner half is a plain <c>GROUP BY</c> the Cosmos rules push, and the count finishes
    /// outside. These tests plan for the asynchronous convention — unlike
    /// <see cref="CosmosPlannerTests"/> — because a finishing aggregate needs somewhere outside the
    /// Cosmos convention to live.
    /// </remarks>
    [TestClass]
    public class CosmosPartialAggregatePlannerTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        CosmosTable _products = null!;

        [TestInitialize]
        public void Initialize()
        {
            _products = new CosmosTable(Products);
        }

        RelNode PlanLogical(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _products);

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
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).project();
        }

        /// <summary>
        /// Plans for the asynchronous convention, with the container's rules and the CLR ones.
        /// </summary>
        RelNode Plan(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_products.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrAsyncEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        static T? Find<T>(RelNode rel) where T : class
        {
            if (rel is T found)
                return found;

            var inputs = rel.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (Find<T>((RelNode)inputs.get(i)) is T inner)
                    return inner;

            return null;
        }

        /// <summary>
        /// Renders the statement a pushed subtree would execute.
        /// </summary>
        string Render(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), Products);
            implementor.Visit(rel);
            return implementor.Build().Sql;
        }

        /// <remarks>
        /// The whole feature in one query: the dedup happens at the service, one row per distinct
        /// value crosses the wire, and the count finishes outside. Without the expansion the only
        /// plan reads every document and counts in process.
        /// </remarks>
        [TestMethod]
        public void CountDistinctPlansAsAPushedGroupByFinishedAbove()
        {
            var plan = Plan("SELECT COUNT(DISTINCT c.\"category\") AS n FROM products AS c");

            var pushed = Find<CosmosAggregate>(plan);
            pushed.Should().NotBeNull("the dedup half should be pushed");
            pushed!.getAggCallList().size().Should().Be(0, "what is pushed is the GROUP BY, not the count");
            pushed.getGroupSet().cardinality().Should().Be(1);

            Render(pushed).Should().Be("SELECT c.category AS \"category\" FROM products c GROUP BY c.category");

            // The finishing count lives outside the Cosmos convention.
            plan.getConvention().Should().Be(ClrAsyncEnumerableConvention.Instance);
        }

        /// <remarks>
        /// The guard the expansion depends on: aggregate output is not addressable as document
        /// paths, so an aggregate above a pushed aggregate must not itself convert — before the
        /// guard it passed the rule's predicate, which inspected only the calls, and failed at
        /// implementation after the plan was chosen.
        /// </remarks>
        [TestMethod]
        public void AnAggregateAboveAPushedAggregateIsNotItselfPushed()
        {
            var plan = Plan(
                "SELECT COUNT(*) AS n FROM (" +
                "SELECT c.\"category\" FROM products AS c GROUP BY c.\"category\") AS g");

            var pushed = Find<CosmosAggregate>(plan);
            pushed.Should().NotBeNull("the inner GROUP BY should still be pushed");
            pushed!.getInput().Should().NotBeOfType<CosmosAggregate>();
            pushed.getAggCallList().size().Should().Be(0);

            plan.getConvention().Should().Be(ClrAsyncEnumerableConvention.Instance);
        }

    }

}
