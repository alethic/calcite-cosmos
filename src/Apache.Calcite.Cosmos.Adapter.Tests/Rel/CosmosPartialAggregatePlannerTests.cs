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

        /// <remarks>
        /// Cosmos groups one way per statement, so a rollup is split: the finest grouping is pushed
        /// as a plain <c>GROUP BY</c> and the grouping sets are computed above it, over one row per
        /// group rather than one per document. The finishing count is a <c>$SUM0</c> of the partial
        /// counts — summed, not recounted, and zero for an empty grand total as <c>COUNT</c> is.
        /// </remarks>
        [TestMethod]
        public void RollupPlansAsAPushedGroupByRolledUpAbove()
        {
            var plan = Plan("SELECT c.\"category\", COUNT(*) AS n FROM products AS c GROUP BY ROLLUP(c.\"category\")");

            var pushed = Find<CosmosAggregate>(plan);
            pushed.Should().NotBeNull("the finest grouping should be pushed");
            pushed!.getGroupType().Should().Be(org.apache.calcite.rel.core.Aggregate.Group.SIMPLE);

            Render(pushed).Should().Be("SELECT c.category AS \"category\", COUNT(1) AS \"n\" FROM products c GROUP BY c.category");

            var text = org.apache.calcite.plan.RelOptUtil.toString(plan);
            text.Should().Contain("groups=[[{0}, {}]]", "the grouping sets are finished above");
            text.Should().Contain("$SUM0", "a partial count is summed, not recounted");

            plan.getConvention().Should().Be(ClrAsyncEnumerableConvention.Instance);
        }

        /// <remarks>
        /// The calls that finish as themselves, over a non-nullable column so the partial is
        /// faithful.
        /// </remarks>
        [TestMethod]
        public void RollupOfSumAndMaxFinishesWithTheSameFunctions()
        {
            var plan = Plan("SELECT c.\"category\", SUM(c.\"_ts\") AS s, MAX(c.\"_ts\") AS m FROM products AS c GROUP BY ROLLUP(c.\"category\")");

            var pushed = Find<CosmosAggregate>(plan);
            pushed.Should().NotBeNull();
            pushed!.getAggCallList().size().Should().Be(2);

            Render(pushed).Should().Be("SELECT c.category AS \"category\", SUM(c._ts) AS \"s\", MAX(c._ts) AS \"m\" FROM products c GROUP BY c.category");

            plan.getConvention().Should().Be(ClrAsyncEnumerableConvention.Instance);
        }

        /// <remarks>
        /// An average of averages weights every group equally, so <c>AVG</c> has no finishing form
        /// of its own — and unreduced, a grouping-set <c>AVG</c> cannot be implemented by the
        /// asynchronous convention at all. <c>AGGREGATE_REDUCE_FUNCTIONS</c> decomposes it into
        /// <c>SUM</c> and <c>COUNT</c>, whose partials push and finish, with the division above.
        /// </remarks>
        [TestMethod]
        public void RollupOfAvgIsSplitThroughSumAndCount()
        {
            var plan = Plan("SELECT c.\"category\", AVG(c.\"_ts\") AS a FROM products AS c GROUP BY ROLLUP(c.\"category\")");

            var pushed = Find<CosmosAggregate>(plan);
            pushed.Should().NotBeNull("the reduced form's partials are pushable");

            // COUNT(1) rather than COUNT(c._ts): Calcite rewrites a COUNT of a non-nullable column
            // to COUNT(*) before any rule sees it.
            Render(pushed!).Should().Contain("SUM(c._ts)").And.Contain("COUNT(1)").And.Contain("GROUP BY c.category");

            plan.getConvention().Should().Be(ClrAsyncEnumerableConvention.Instance);
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
        /// <c>GROUP BY</c> is applied before <c>OFFSET</c>/<c>LIMIT</c> in Cosmos, so an aggregate
        /// above a pushed row limit would group the whole container rather than the five rows. The
        /// rule must decline and leave the count outside — before it did, the plan converted and
        /// failed at implementation, after it was chosen.
        /// </remarks>
        [TestMethod]
        public void AnAggregateAboveAPushedRowLimitIsNotPushed()
        {
            var plan = Plan("SELECT COUNT(*) AS n FROM (SELECT * FROM products LIMIT 5) AS g");

            Find<CosmosAggregate>(plan).Should().BeNull("grouping happens before the limit at the service");
            Find<CosmosSort>(plan).Should().NotBeNull("the limit itself is still pushed");
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
