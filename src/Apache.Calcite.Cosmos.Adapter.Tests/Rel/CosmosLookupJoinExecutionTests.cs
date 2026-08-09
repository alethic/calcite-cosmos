using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;
using Apache.Calcite.Extensions.Adapter.Enumerable;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite;
using org.apache.calcite.adapter.java;
using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.plan.volcano;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Runs a lookup join: a real plan, compiled, against stub executors.
    /// </summary>
    /// <remarks>
    /// The planner tests say the shape is chosen and the runtime tests say the batching is right. This
    /// is what says the two are connected — that the statement the rule's node renders is the statement
    /// the service is given, carrying the keys the build side actually had.
    /// </remarks>
    [TestClass]
    public class CosmosLookupJoinExecutionTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });
        static readonly CosmosContainerMetadata Orders = new("orders", new[] { "/customer" });

        /// <summary>
        /// Answers with documents and records every statement it was given.
        /// </summary>
        sealed class StubExecutor : ICosmosQueryExecutor
        {

            readonly string[] _documents;

            public StubExecutor(params string[] documents)
            {
                _documents = documents;
            }

            public List<CosmosQuery> Executed { get; } = new();

            public async IAsyncEnumerable<JsonElement> ExecuteAsync(CosmosQuery query, PartitionKey? partitionKey = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Executed.Add(query);

                foreach (var document in _documents)
                {
                    await Task.Yield();
                    yield return JsonDocument.Parse(document).RootElement.Clone();
                }
            }

        }

        sealed class TestDataContext : DataContext
        {

            readonly SchemaPlus _rootSchema;
            readonly JavaTypeFactory _typeFactory;

            public TestDataContext(SchemaPlus rootSchema, JavaTypeFactory typeFactory)
            {
                _rootSchema = rootSchema;
                _typeFactory = typeFactory;
            }

            public SchemaPlus getRootSchema() => _rootSchema;

            public JavaTypeFactory getTypeFactory() => _typeFactory;

            public org.apache.calcite.linq4j.QueryProvider getQueryProvider() => null!;

            public object get(string name) => null!;

        }

        CosmosTable _products = null!;
        CosmosTable _orders = null!;
        StubExecutor _productsExecutor = null!;
        StubExecutor _ordersExecutor = null!;
        CalciteSchema _rootSchema = null!;
        JavaTypeFactoryImpl _typeFactory = null!;

        void Given(string[] orders, string[] products)
        {
            _typeFactory = new JavaTypeFactoryImpl();

            _ordersExecutor = new StubExecutor(orders);
            _productsExecutor = new StubExecutor(products);

            _orders = new CosmosTable(Orders, _ordersExecutor);
            _products = new CosmosTable(Products, _productsExecutor);

            _rootSchema = CalciteSchema.createRootSchema(false);
            _rootSchema.add("orders", _orders);
            _rootSchema.add("products", _products);
        }

        RelNode Plan(string sql)
        {
            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(_rootSchema, java.util.Collections.emptyList(), _typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();
            var validator = SqlValidatorUtil.newValidator(SqlStdOperatorTable.instance(), catalogReader, _typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(org.apache.calcite.rel.RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(_typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var rule in CosmosRules.GetRules(_orders.Convention))
                planner.addRule(rule);

            foreach (var rule in CosmosRules.GetRules(_products.Convention))
                planner.addRule(rule);

            foreach (var rule in ClrAsyncEnumerableRules.Rules())
                planner.addRule(rule);


            var desired = logical.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return ToCalc(planner.findBestExp());
        }

        /// <summary>
        /// Turns whatever projections and filters survived the cost-based phase into calcs.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is Calcite's <c>Programs.CALC_PROGRAM</c>, and it is a pass <em>after</em> the planner
        /// rather than rules given to it. That distinction is the whole of it:
        /// <c>ClrAsyncEnumerableProject</c> throws when implemented — as
        /// <c>EnumerableProject.implement()</c> does upstream, saying "EnumerableCalcRel is always
        /// better" — and it is also the cheaper node, since <c>Calc</c>'s inherited cost counts one
        /// unit per expression and <c>Project</c>'s does not. Handed to Volcano, the two compete and
        /// the throwing one wins on cost. Run afterwards, there is no competition: every projection
        /// left standing becomes a calc.
        /// </para>
        /// <para>
        /// Invisible until there is a join, because until then every projection is pushed into the
        /// container and none survives to be implemented.
        /// </para>
        /// </remarks>
        static RelNode ToCalc(RelNode rel)
        {
            var program = new org.apache.calcite.plan.hep.HepProgramBuilder();

            foreach (var rule in ClrAsyncEnumerableRules.CalcRules())
                program.addRuleInstance(rule);

            var hep = new org.apache.calcite.plan.hep.HepPlanner(program.build());
            hep.setRoot(rel);

            return hep.findBestExp();
        }

        async Task<List<object>> Execute(RelNode rel)
        {
            var implementor = new ClrAsyncEnumerableRelImplementor(rel.getCluster().getRexBuilder(), new java.util.HashMap());
            var lambda = implementor.ImplementRoot((ClrAsyncEnumerableRel)rel, ClrEnumerablePrefer.Array);

            var run = (Func<DataContext, IAsyncEnumerable<object>>)lambda.Compile();
            var context = new TestDataContext(_rootSchema.plus(), _typeFactory);

            var rows = new List<object>();
            await foreach (var row in run(context))
                rows.Add(row);

            return rows;
        }

        static object?[] Keys(CosmosQuery query) =>
            query.Parameters.Where(p => p.Name.StartsWith(CosmosLookupJoin.KeyPrefix)).Select(p => p.Value).ToArray();

        // ── The whole path ────────────────────────────────────────────────────────

        /// <remarks>
        /// Three orders and three products, matching on two of them. What is being checked is not only
        /// that two rows come back, but that the container was asked for the two keys the orders had —
        /// which is the difference between this feature working and it being an expensive no-op.
        /// </remarks>
        [TestMethod]
        public async Task AJoinFetchesOnlyTheKeysTheOtherSideHas()
        {
            Given(
                orders: new[]
                {
                    """{"_MAP":{"id":"a"},"id":"a","_ts":1,"_etag":"e","customer":"c1"}""",
                    """{"_MAP":{"id":"b"},"id":"b","_ts":1,"_etag":"e","customer":"c2"}""",
                },
                products: new[]
                {
                    """{"_MAP":{"id":"a"},"id":"a","_ts":1,"_etag":"e","category":"bikes"}""",
                    """{"_MAP":{"id":"b"},"id":"b","_ts":1,"_etag":"e","category":"shoes"}""",
                });

            var plan = Plan("SELECT o.id, p.category FROM orders o JOIN products p ON o.id = p.id");

            var rows = await Execute(plan);
            rows.Should().HaveCount(2);

            // The orders side is read whole, as the build side must be.
            _ordersExecutor.Executed.Should().ContainSingle();

            // The products side is asked once, for exactly the keys the orders had — padded to the
            // statement's fixed parameter count with a key it already carries.
            _productsExecutor.Executed.Should().ContainSingle();

            var statement = _productsExecutor.Executed[0];
            statement.Sql.Should().Contain($"IN ({CosmosLookupJoin.KeyPrefix}0, ");

            var keys = Keys(statement);
            keys.Should().HaveCount(CosmosLookupJoin.DefaultBatchSize);
            keys.Distinct().Should().BeEquivalentTo(new object?[] { "a", "b" });
        }

        /// <remarks>
        /// The join is still a join: a build row whose key no document has contributes nothing, and a
        /// document no build row asked for is not returned to begin with.
        /// </remarks>
        [TestMethod]
        public async Task OnlyMatchingPairsAreProduced()
        {
            Given(
                orders: new[]
                {
                    """{"_MAP":{"id":"a"},"id":"a","_ts":1,"_etag":"e","customer":"c1"}""",
                    """{"_MAP":{"id":"missing"},"id":"missing","_ts":1,"_etag":"e","customer":"c2"}""",
                },
                products: new[] { """{"_MAP":{"id":"a"},"id":"a","_ts":1,"_etag":"e","category":"bikes"}""" });

            var rows = await Execute(Plan("SELECT o.id, p.category FROM orders o JOIN products p ON o.id = p.id"));

            rows.Should().ContainSingle();
        }

        /// <remarks>
        /// A build side with no rows never asks the container anything at all, which is the extreme case
        /// of the saving and the one a rendered predicate could not have reached.
        /// </remarks>
        [TestMethod]
        public async Task AnEmptyBuildSideAsksTheContainerNothing()
        {
            Given(orders: System.Array.Empty<string>(), products: new[] { """{"_MAP":{"id":"a"},"id":"a","_ts":1,"_etag":"e","category":"bikes"}""" });

            var rows = await Execute(Plan("SELECT o.id, p.category FROM orders o JOIN products p ON o.id = p.id"));

            rows.Should().BeEmpty();
            _productsExecutor.Executed.Should().BeEmpty();
        }

    }

}
