using System.Linq;

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
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Covers whether the planner actually chooses to fetch by key rather than read the container.
    /// </summary>
    /// <remarks>
    /// The rule and the node can both be right and the feature still not happen, because a rule that is
    /// never chosen is a rule that does nothing. These are the tests that say it is chosen — and, as
    /// importantly, that it is not chosen where fetching per batch would answer a different question.
    /// </remarks>
    [TestClass]
    public class CosmosLookupJoinPlannerTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });
        static readonly CosmosContainerMetadata Orders = new("orders", new[] { "/customer" });

        CosmosTable _products = null!;
        CosmosTable _orders = null!;

        [TestInitialize]
        public void Initialize()
        {
            _products = new CosmosTable(Products);
            _orders = new CosmosTable(Orders);
        }

        RelNode PlanLogical(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _products);
            rootSchema.add("orders", _orders);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = org.apache.calcite.sql.util.SqlOperatorTables.chain(
                org.apache.calcite.sql.fun.SqlStdOperatorTable.instance(),
                Apache.Calcite.Cosmos.Adapter.Sql.CosmosOperators.Instance);

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            // The CLR join rules include a merge join, which asks for the collation of its inputs and
            // fails outright where the trait is not registered. Nothing in this adapter produces a
            // collation, but the rule still has to be able to ask.
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).project();
        }

        /// <summary>
        /// Plans for the asynchronous convention, with both containers' rules and the CLR ones.
        /// </summary>
        RelNode Plan(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_products.Convention))
                planner.addRule(rule);

            foreach (var rule in CosmosRules.GetRules(_orders.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrAsyncEnumerableRules.Rules())
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        static string Text(RelNode rel) => RelOptUtil.toString(rel).Trim().Replace("\r\n", "\n");

        static bool Contains<T>(RelNode rel) where T : class, RelNode => Find<T>(rel) is not null;

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

        // ── Chosen ────────────────────────────────────────────────────────────────

        /// <remarks>
        /// The whole feature in one query: two containers joined on a key both address, where without
        /// this the plan reads every document of both.
        /// </remarks>
        [TestMethod]
        public void AnEquiJoinOnAnAddressableKeyBecomesALookup()
        {
            var plan = Plan("SELECT * FROM orders o JOIN products p ON o.id = p.id");

            var lookup = Find<CosmosLookupJoin>(plan);
            lookup.Should().NotBeNull("the plan should fetch by key rather than read the container:\n" + Text(plan));

            // The probe side is the container, and the build side is not — a lookup whose sides were
            // the other way round would plan, run, and read the container whole.
            lookup!.getRight().getConvention().Should().BeSameAs(_products.Convention);
        }

        // ── Not chosen, and each for a reason ─────────────────────────────────────

        /// <remarks>
        /// <c>TOP 5</c> of the container is not <c>TOP 5</c> of each batch. Running it per batch would
        /// return rows the plan never asked for, so the restriction is refused and both sides are read.
        /// </remarks>
        [TestMethod]
        public void AJoinAgainstALimitedSubqueryIsNotALookup()
        {
            var plan = Plan("SELECT * FROM orders o JOIN (SELECT * FROM products FETCH FIRST 5 ROWS ONLY) p ON o.id = p.id");

            Contains<CosmosLookupJoin>(plan).Should().BeFalse("a limit is defined over the container, not over a batch:\n" + Text(plan));
        }

        /// <remarks>
        /// An outer join has to preserve build rows with no match. That is expressible and not written,
        /// so it declines rather than quietly dropping them.
        /// </remarks>
        [TestMethod]
        public void ALeftJoinIsNotALookup()
        {
            var plan = Plan("SELECT * FROM orders o LEFT JOIN products p ON o.id = p.id");

            Contains<CosmosLookupJoin>(plan).Should().BeFalse("only inner joins are implemented:\n" + Text(plan));
        }

        /// <remarks>
        /// A partition key column is typed <c>ANY</c>, which says nothing about what would be bound as a
        /// parameter or compared once fetched.
        /// </remarks>
        [TestMethod]
        public void AJoinOnAnUntypedColumnIsNotALookup()
        {
            var plan = Plan("SELECT * FROM orders o JOIN products p ON o.customer = p.category");

            Contains<CosmosLookupJoin>(plan).Should().BeFalse("an ANY key cannot be bound as a parameter:\n" + Text(plan));
        }

        /// <remarks>
        /// A non-equality is not a key. There is nothing to put in an <c>IN</c>.
        /// </remarks>
        [TestMethod]
        public void ANonEquiJoinIsNotALookup()
        {
            var plan = Plan("SELECT * FROM orders o JOIN products p ON o.id < p.id");

            Contains<CosmosLookupJoin>(plan).Should().BeFalse("there is no key to fetch by:\n" + Text(plan));
        }

    }

}
