using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

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

            // Chained so that a query can name the adapter's own functions. Calcite's standard table has
            // nothing to resolve IS_DEFINED or FULLTEXTCONTAINS to, and this is the seam a caller wires
            // the same way.
            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                SqlStdOperatorTable.instance(), Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance);

            var validator = SqlValidatorUtil.newValidator(
                operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            // project(), not rel. Ordering by an expression outside the select list makes
            // SqlToRelConverter carry it as an extra column and record in the RelRoot that it is not
            // output; project() is what applies that, and is what a real consumer uses. Taking rel
            // leaves the column at the root, which for a scoring function is the difference between a
            // plan that can be implemented and one that cannot — Cosmos will not project a score.
            // It is a no-op wherever the mapping is trivial, which is every other query here.
            return converter.convertQuery(validator.validate(parsed), false, true).project();
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

        /// <summary>
        /// Renders a planned tree to the full query, including anything recovered about execution.
        /// </summary>
        CosmosQuery Query(RelNode rel)
        {
            var implementor = new CosmosImplementor(rel.getCluster().getRexBuilder(), Products);
            implementor.Visit(rel);
            return implementor.Build();
        }

        // ── Partition key recovery ────────────────────────────────────────────────

        /// <remarks>
        /// Naming the partition key confines execution to one physical partition rather than
        /// fanning out across every one. It changes nothing about the statement itself.
        /// </remarks>
        [TestMethod]
        public void PredicateOnThePartitionKeyIsRecovered()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes'"));

            query.PartitionKeyValues.Should().Equal("bikes");
            query.Sql.Should().Contain("WHERE (c.category = @p0)");
        }

        [TestMethod]
        public void PredicateOnANonPartitionKeyRecoversNothing()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'")).PartitionKeyValues.Should().BeNull();
        }

        [TestMethod]
        public void QueryWithoutAPredicateRecoversNothing()
        {
            Query(PlanToCosmos("SELECT * FROM products")).PartitionKeyValues.Should().BeNull();
        }

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

        /// <remarks>
        /// A <c>HAVING</c> on a grouping key is a filter above the aggregate, which cannot bind —
        /// aggregate output has no document paths. <c>FILTER_AGGREGATE_TRANSPOSE</c> moves it below,
        /// where it is an ordinary <c>WHERE</c> the service applies before grouping, and where a
        /// predicate on the partition key confines execution the way it does anywhere else.
        /// </remarks>
        [TestMethod]
        public void HavingOnAGroupingKeyIsPushedAsAWhere()
        {
            var query = Query(PlanToCosmos(
                "SELECT c.\"category\", COUNT(*) AS n FROM products AS c GROUP BY c.\"category\" HAVING c.\"category\" = 'bikes'"));

            query.Sql.Should().Be("SELECT c.category AS \"category\", COUNT(1) AS \"n\" FROM products c WHERE (c.category = @p0) GROUP BY c.category");
            query.PartitionKeyValues.Should().Equal("bikes");
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
        /// Not by any rule here: Calcite rewrites <c>COUNT(x)</c> over a non-nullable column to
        /// <c>COUNT(*)</c> before conversion, so what arrives is the argumentless form that is
        /// always safe. Pinned because it is why <see cref="CosmosAggregate.CanImplement"/> needs
        /// no non-nullable <c>COUNT(x)</c> case — that branch was probed and found dead.
        /// </remarks>
        [TestMethod]
        public void CountOfANonNullableColumnIsPushedDown()
        {
            var sql = Render(PlanToCosmos("SELECT COUNT(c.\"_ts\") AS n FROM products AS c"));

            sql.Should().Be("SELECT COUNT(1) AS \"n\" FROM products c");
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

        /// <remarks>
        /// Not pushed <em>whole</em>: the registered expansion rewrites this into an aggregate over
        /// an aggregate whose inner <c>GROUP BY</c> is pushable, but the finishing count has no
        /// Cosmos rendering, and a planner asked for a wholly-Cosmos plan cannot produce one. The
        /// partial form is covered in <see cref="CosmosPartialAggregatePlannerTests"/>, where there
        /// is somewhere outside the convention for the count to live.
        /// </remarks>
        [TestMethod]
        public void DistinctAggregateIsNotPushedDownWhole()
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

        // ── Binding through a projection ────────────────────────

        /// <remarks>
        /// The control for the two below. The key is <c>id</c> rather than <c>category</c> because a
        /// nullable column is refused on its null placement, whatever the projection does.
        /// </remarks>
        [TestMethod]
        public void SortPushesPastAnAllPathProjection()
        {
            var best = PlanToCosmos("SELECT c.\"id\" AS \"i\" FROM products AS c ORDER BY c.\"id\"");

            Render(best).Should().Contain("ORDER BY c.id ASC");
        }

        /// <remarks>
        /// A computed column has no document path, and the columns beside it still do. Binding per
        /// ordinal is what lets this sort push: the key names <c>id</c>, a plain path, and never reads
        /// the computed one. Bound all-or-nothing, as it was, the whole sort was declined.
        /// </remarks>
        [TestMethod]
        public void SortOnAPlainColumnPushesPastAComputedProjection()
        {
            var best = PlanToCosmos("SELECT UPPER(c.\"id\") AS \"u\", c.\"id\" AS \"i\" FROM products AS c ORDER BY c.\"id\"");

            Render(best).Should().Contain("ORDER BY c.id ASC");
        }

        /// <remarks>
        /// The other half of the same rule: a sort that does read the computed column has nothing to
        /// order by, Cosmos being unable to address a projection alias, so it is refused — <b>at the
        /// rule</b>, which is what <c>CosmosConverterRule</c> requires. The rule derives its binding by
        /// walking the input rather than reading alias names off the input row type, so it declines here
        /// for the same reason implementation would, and no plan is produced to render.
        /// </remarks>
        [TestMethod]
        public void SortOnAComputedColumnIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT UPPER(c.\"id\") AS \"u\", c.\"id\" AS \"i\" FROM products AS c ORDER BY UPPER(c.\"id\")");

            act.Should().Throw<Exception>();
        }


        // ── Partial filter pushdown ───────────────────────────────

        /// <summary>
        /// Plans a statement, asking for the asynchronous convention so that a plan may legitimately
        /// keep some work in Calcite rather than having to be Cosmos throughout.
        /// </summary>
        RelNode PlanToAsync(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            foreach (var rule in Apache.Calcite.Extensions.Adapter.AsyncEnumerable.ClrAsyncEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(Apache.Calcite.Extensions.Adapter.AsyncEnumerable.ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <remarks>
        /// INITCAP has no Cosmos form, so the whole predicate used to be declined and every document
        /// crossed the wire. The renderable conjunct is pushed and the rest rechecked above it, which is
        /// sound because dropping a conjunct only ever weakens: the service discards nothing the full
        /// predicate would have kept.
        /// </remarks>
        [TestMethod]
        public void RenderablePartOfAPredicateIsPushedAndTheRestRechecked()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND INITCAP(c.\"id\") = 'X'");
            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter");
            plan.Should().Contain("INITCAP");
        }

        /// <summary>
        /// A disjunction whose branch cannot be rendered is pushed as what that branch implies.
        /// </summary>
        /// <remarks>
        /// Dropping a disjunct strengthens, so an <c>OR</c> with an untranslatable branch used to be
        /// declined whole and every document crossed the wire. Since a branch can only be true where
        /// the paths it reads are defined, <c>a OR b</c> pushes <c>a OR IS_DEFINED(…)</c> — implied by
        /// the original, so it discards nothing the original would have kept — and the original is
        /// rechecked above it.
        /// </remarks>
        [TestMethod]
        public void ADisjunctionWithAnUntranslatableBranchIsWeakened()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' OR INITCAP(c.\"_etag\") = 'X'");
            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter", "something should reach the service: " + plan);
            plan.Should().Contain("IS_DEFINED", "the untranslatable branch implies its path is defined: " + plan);
            plan.Should().Contain("INITCAP", "and the original is still rechecked: " + plan);
        }

        /// <summary>
        /// A branch that can observe absence implies nothing about definedness, and is not weakened.
        /// </summary>
        /// <remarks>
        /// Measured, and the reason the rule is about absence rather than about polarity:
        /// <c>NOT IS_DEFINED(c.x)</c> is true exactly where the path is missing, so a branch containing
        /// it cannot imply the path is there. Weakening it anyway would strengthen the predicate and
        /// lose rows — the failure this whole design is arranged to make impossible.
        /// </remarks>
        [TestMethod]
        public void ABranchThatObservesAbsenceIsNotWeakened()
        {
            var best = PlanToAsync(
                "SELECT * FROM products AS c " +
                "WHERE c.\"category\" = 'bikes' OR (NOT IS_DEFINED(c.\"_etag\") AND INITCAP(c.\"_etag\") = 'X')");

            var plan = Plan(best);

            plan.Should().NotContain("CosmosFilter",
                "nothing about this disjunction is safe to push: " + plan);
        }

        /// <summary>
        /// SQL's own null tests observe absence, and a branch using one is not weakened either.
        /// </summary>
        /// <remarks>
        /// <c>x IS NULL</c> renders as <c>(NOT IS_DEFINED(x) OR IS_NULL(x))</c>, so it is true where
        /// the path is missing — but its Rex operator belongs to the standard table rather than to the
        /// Cosmos family, so checking only the latter let it through. A branch containing one can be
        /// true with the path absent, and weakening it to <c>IS_DEFINED</c> would have discarded
        /// exactly those rows.
        /// </remarks>
        [TestMethod]
        public void ABranchUsingSqlNullTestsIsNotWeakened()
        {
            var best = PlanToAsync(
                "SELECT * FROM products AS c " +
                "WHERE c.\"id\" = 'x' OR (c.\"category\" IS NULL AND INITCAP(c.\"_etag\") = 'X')");

            var plan = Plan(best);

            plan.Should().NotContain("CosmosFilter",
                "a branch that can be true with the path absent implies nothing about definedness: " + plan);
        }

        /// <summary>
        /// A sort above a pushed aggregate stays in Calcite, and the aggregate still pushes.
        /// </summary>
        /// <remarks>
        /// Cosmos rejects <c>GROUP BY</c> and <c>ORDER BY</c> in one statement. <c>CosmosSort</c>
        /// refuses the combination when it renders, but refusing only there is too late — the rule
        /// would already have produced a node the planner cannot implement, which fails rather than
        /// planning something slower. Declining in the rule is also the better plan: the sort then runs
        /// over one row per group instead of over the container.
        /// </remarks>
        [TestMethod]
        public void ASortAboveAPushedAggregateStaysInCalcite()
        {
            var best = PlanToAsync("SELECT c.\"category\", COUNT(*) FROM products AS c GROUP BY c.\"category\" ORDER BY c.\"category\"");
            var plan = Plan(best);

            plan.Should().Contain("CosmosAggregate", "the grouping is still worth pushing: " + plan);
            plan.Should().NotContain("CosmosSort", "Cosmos will not take ORDER BY alongside GROUP BY: " + plan);
        }

        /// <summary>
        /// The same split applies to a filter sitting above a projection.
        /// </summary>
        /// <remarks>
        /// The argument does not depend on what the filter sits on — dropping a conjunct only ever
        /// weakens, so the service discards nothing the full predicate would have kept. The rule used
        /// to match a filter directly over the scan and nothing else, which meant a projection between
        /// the two cost the whole pushdown rather than the untranslatable half of it.
        /// </remarks>
        [TestMethod]
        public void APredicateAboveAProjectionIsSplitToo()
        {
            var best = PlanToAsync(
                "SELECT * FROM (SELECT c.\"category\" AS cat, c.\"id\" AS ident FROM products AS c) AS t " +
                "WHERE t.cat = 'bikes' AND INITCAP(t.ident) = 'X'");

            var plan = Plan(best);

            plan.Should().Contain("CosmosFilter");
            plan.Should().Contain("INITCAP");
        }

        /// <remarks>
        /// The pushed half is the renderable conjunct alone, and the partition key it pins is still
        /// recovered from it.
        /// </remarks>
        [TestMethod]
        public void ThePushedHalfCarriesOnlyTheRenderableConjunct()
        {
            var best = PlanToAsync("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND INITCAP(c.\"id\") = 'X'");
            var cosmos = FindCosmos(best);

            var query = Query(cosmos);
            query.Sql.Should().Contain("WHERE (c.category = @p0)");
            query.Sql.Should().NotContain("INITCAP");
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// A wholly renderable predicate is not split; there is nothing to leave behind.
        /// </remarks>
        [TestMethod]
        public void AWhollyRenderablePredicateIsNotSplit()
        {
            var best = PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes' AND c.\"id\" = 'x'");

            Plan(best).Split("CosmosFilter").Length.Should().Be(2);
        }

        /// <summary>
        /// Returns the root of the pushed-down Cosmos subtree — the highest node in the convention,
        /// which is the one that renders the whole statement.
        /// </summary>
        static RelNode FindCosmos(RelNode node)
        {
            if (node is CosmosRel)
                return node;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (FindCosmos((RelNode)inputs.get(i)) is RelNode found)
                    return found;

            return null!;
        }


        // ── Navigating the document ───────────────────────────────────────────────

        /// <remarks>
        /// The map column's type is one level — MAP&lt;VARCHAR, ANY&gt; — and the document beneath it is
        /// not. Depth still resolves because ITEM over ANY is ANY, so the chain type-checks, and the
        /// translator folds the whole chain into one path rather than nesting accessors.
        /// </remarks>
        [TestMethod]
        public void NestedPropertiesResolveToASinglePath()
        {
            var best = PlanToCosmos("SELECT c.\"_MAP\"['metadata']['sku'] AS \"sku\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"sku\": c.metadata.sku } FROM products c");
        }

        [TestMethod]
        public void ThreeLevelsResolveJustAsFar()
        {
            var best = PlanToCosmos("SELECT c.\"_MAP\"['a']['b']['c'] AS \"deep\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"deep\": c.a.b.c } FROM products c");
        }

        /// <remarks>
        /// An array index is a path segment like any other.
        /// </remarks>
        [TestMethod]
        public void ArrayIndexingIsPartOfThePath()
        {
            var best = PlanToCosmos("SELECT c.\"_MAP\"['tags'][0] AS \"first\" FROM products AS c");

            Render(best).Should().Be("SELECT VALUE { \"first\": c.tags[0] } FROM products c");
        }

        /// <remarks>
        /// A non-constant key has no path form — the statement addresses a property by name, and the
        /// name is not known until the row is read — so it is declined rather than guessed at.
        /// </remarks>
        [TestMethod]
        public void ANonConstantKeyIsNotAPath()
        {
            var act = () => PlanToCosmos("SELECT c.\"_MAP\"[c.\"id\"] AS \"dynamic\" FROM products AS c");

            act.Should().Throw<Exception>();
        }


        // ── Testing whether a property exists ─────────────────────────────────────

        /// <remarks>
        /// The SQL spelling. Cosmos distinguishes an absent property from one present and null and SQL
        /// does not, so this renders as both tests — which is what makes it match a document that simply
        /// lacks the property.
        /// </remarks>
        [TestMethod]
        public void IsNotNullOnAMapPropertyTestsBothCosmosStates()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE c.\"_MAP\"['metadata'] IS NOT NULL");

            Render(best).Should().Contain("(IS_DEFINED(c.metadata) AND NOT IS_NULL(c.metadata))");
        }

        /// <remarks>
        /// The exact spelling, for a query that needs to tell absent from null — which SQL cannot say and
        /// this adapter's own operator can. It reaches the validator through the chained operator table.
        /// </remarks>
        [TestMethod]
        public void IsDefinedTestsExistenceAlone()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(c.\"_MAP\"['metadata'])");

            Render(best).Should().Contain("WHERE IS_DEFINED(c.metadata)");
        }

        /// <remarks>
        /// Existence of a nested property, which is the same question one level down and the same path.
        /// </remarks>
        [TestMethod]
        public void IsDefinedReachesANestedProperty()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE IS_DEFINED(c.\"_MAP\"['metadata']['sku'])");

            Render(best).Should().Contain("WHERE IS_DEFINED(c.metadata.sku)");
        }


        // ── Ranking by a scoring function ─────────────────────────────────────────

        /// <remarks>
        /// Calcite expresses this as three nodes — project the score, sort on it, project it away — and
        /// the first is a statement Cosmos rejects, a scoring function not being projectable. The whole
        /// shape collapses into one clause, and the score never appears in the select list.
        /// </remarks>
        [TestMethod]
        public void OrderingByAScoreBecomesOrderByRank()
        {
            var best = PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') FETCH FIRST 10 ROWS ONLY");
            var sql = Render(best);

            sql.Should().Be("SELECT TOP 10 VALUE { \"id\": c.id } FROM products c ORDER BY RANK FULLTEXTSCORE(c.name, @p0)");
            sql.Should().NotContain("\"$f");
        }

        /// <remarks>
        /// The keyword binds like any other literal, so the statement text does not vary with it.
        /// </remarks>
        [TestMethod]
        public void TheRankKeywordIsBound()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\" FROM products AS c ORDER BY FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') FETCH FIRST 5 ROWS ONLY"));

            query.Parameters.Should().ContainSingle().Which.Value.Should().Be("steel");
        }

        /// <remarks>
        /// RRF fuses two scores, and its arguments are themselves scoring functions rather than paths.
        /// </remarks>
        [TestMethod]
        public void RrfFusesTwoScores()
        {
            var best = PlanToCosmos(
                "SELECT c.\"id\" FROM products AS c " +
                "ORDER BY RRF(FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel'), FULLTEXTSCORE(c.\"_MAP\"['tags'], 'frame')) " +
                "FETCH FIRST 10 ROWS ONLY");

            Render(best).Should().Contain("ORDER BY RANK RRF(FULLTEXTSCORE(c.name, @p0), FULLTEXTSCORE(c.tags, @p1))");
        }

        /// <remarks>
        /// A scoring function anywhere but the rank clause is refused: the service will not project one,
        /// and will not filter on one either.
        /// </remarks>
        [TestMethod]
        public void AProjectedScoreIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') AS \"s\" FROM products AS c");

            act.Should().Throw<Exception>();
        }

        [TestMethod]
        public void AScoreInAPredicateIsNotPushedDown()
        {
            var act = () => PlanToCosmos("SELECT c.\"id\" FROM products AS c WHERE FULLTEXTSCORE(c.\"_MAP\"['name'], 'steel') > 1");

            act.Should().Throw<Exception>();
        }


        // ── Point lookup ──────────────────────────────────────────────────────────

        /// <remarks>
        /// A lookup by id and a complete partition key is a read, not a query: about 1 RU against the
        /// 2.3 a query costs at best, and no query engine.
        /// </remarks>
        [TestMethod]
        public void IdAndPartitionKeyBecomeAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"category\" = 'bikes'"));

            query.PointReadId.Should().Be("x");
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        /// <remarks>
        /// <b>The predicate must say nothing else.</b> A point read applies no predicate of its own, so
        /// under an extra conjunct it would return a document the query excludes — a wrong answer rather
        /// than a slow one. The statement is still rendered and still executed; it is just executed as a
        /// query.
        /// </remarks>
        [TestMethod]
        public void AResidualPredicateRulesOutAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"category\" = 'bikes' AND c.\"_ts\" > 100"));

            query.PointReadId.Should().BeNull();
            query.PartitionKeyValues.Should().Equal("bikes");
        }

        [TestMethod]
        public void AnIdWithoutThePartitionKeyIsNotAPointRead()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x'")).PointReadId.Should().BeNull();
        }

        [TestMethod]
        public void APartitionKeyWithoutAnIdIsNotAPointRead()
        {
            Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"category\" = 'bikes'")).PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// A read returns one document whole. A row limit and an ordering describe a result set rather
        /// than a document, so either rules it out even though the predicate would allow it.
        /// </remarks>
        [TestMethod]
        public void ARowLimitRulesOutAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE c.\"id\" = 'x' AND c.\"category\" = 'bikes' FETCH FIRST 1 ROWS ONLY"));

            query.PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// Under a disjunction an equality does not constrain the whole predicate, so it pins nothing —
        /// the same reason the partition key is not recovered from one.
        /// </remarks>
        [TestMethod]
        public void ADisjunctionIsNotAPointRead()
        {
            var query = Query(PlanToCosmos("SELECT * FROM products AS c WHERE (c.\"id\" = 'x' AND c.\"category\" = 'bikes') OR c.\"id\" = 'y'"));

            query.PointReadId.Should().BeNull();
        }

        /// <remarks>
        /// A projection of plain paths still reads: the converter walks each path in the returned
        /// document rather than naming a property of an object the statement never constructed.
        /// </remarks>
        [TestMethod]
        public void AProjectionOfPathsStillPointReads()
        {
            var query = Query(PlanToCosmos("SELECT c.\"id\", c.\"category\" FROM products AS c WHERE c.\"id\" = 'x' AND c.\"category\" = 'bikes'"));

            query.PointReadId.Should().Be("x");
        }


        // ── Hierarchical partition keys ───────────────────────────────────────────

        static readonly CosmosContainerMetadata Tenanted = new("products", new[] { "/tenant", "/user" });

        /// <summary>
        /// Plans against a container whose partition key is hierarchical.
        /// </summary>
        CosmosQuery TenantedQuery(string sql)
        {
            _table = new CosmosTable(Tenanted);

            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            planner.setRoot(planner.changeTraits(logical, logical.getTraitSet().replace(_table.Convention).simplify()));
            var best = planner.findBestExp();

            var implementor = new CosmosImplementor(best.getCluster().getRexBuilder(), Tenanted);
            implementor.Visit(best);
            return implementor.Build();
        }

        /// <remarks>
        /// Cosmos routes on any prefix of a hierarchical key, so pinning the outermost path reaches the
        /// partitions under that tenant rather than every partition in the container. Recovering only a
        /// complete key threw that away.
        /// </remarks>
        [TestMethod]
        public void APinnedOutermostPathRoutesOnThePrefix()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"tenant\" = 'acme'");

            query.PartitionKeyValues.Should().Equal("acme");
            query.PartitionKeyIsComplete.Should().BeFalse();
        }

        [TestMethod]
        public void PinningEveryPathIsACompleteKey()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"tenant\" = 'acme' AND c.\"user\" = 'kim'");

            query.PartitionKeyValues.Should().Equal("acme", "kim");
            query.PartitionKeyIsComplete.Should().BeTrue();
        }

        /// <remarks>
        /// Prefix means prefix. Routing is on the leading components, so pinning an inner path without
        /// the one above it narrows nothing and must not be presented as though it did.
        /// </remarks>
        [TestMethod]
        public void AnInnerPathWithoutTheOuterRoutesNothing()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"user\" = 'kim'");

            query.PartitionKeyValues.Should().BeNull();
        }

        /// <remarks>
        /// A prefix routes to a set of partitions and does not identify a document, so it cannot carry
        /// a point read however much of the predicate is an id.
        /// </remarks>
        [TestMethod]
        public void APrefixCannotCarryAPointRead()
        {
            var query = TenantedQuery("SELECT * FROM products AS c WHERE c.\"tenant\" = 'acme' AND c.\"id\" = 'x'");

            query.PartitionKeyValues.Should().Equal("acme");
            query.PointReadId.Should().BeNull();
        }


        // ── What the table claims about ordering ──────────────────────────────────

        /// <remarks>
        /// A probe, not a specification. RelOptTableImpl.getCollationList returns the statistic's
        /// collations, and RelMdCollation reports them as the collation <em>of a scan</em> — that is,
        /// the order rows already arrive in. Whether the planner is being told that is what this asks.
        /// </remarks>
        [TestMethod]
        public void AScanIsNotClaimedToBeSorted()
        {
            var best = PlanToCosmos("SELECT * FROM products");
            var mq = best.getCluster().getMetadataQuery();

            var collations = mq.collations(best);

            // The container declares a composite index over (/id, /_ts). An index permits an ORDER BY;
            // it does not order a query that has none, and Cosmos guarantees no order without one. A
            // scan claiming a collation would licence the planner to drop a Sort that asked for it.
            collations.Should().NotBeNull();
            collations.size().Should().Be(0, "a Cosmos scan returns rows in no guaranteed order");
        }

    }

}