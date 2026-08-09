using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Sql;

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
    /// Covers the one way out of the Cosmos convention: that the planner finds it, and that a plan
    /// ending in it produces the rows the documents hold.
    /// </summary>
    /// <remarks>
    /// The end-to-end tests compile the plan and run it against a stub executor. Nothing here reaches a
    /// service — that is the point of <see cref="ICosmosQueryExecutor"/> being the seam — but everything
    /// above the seam is the real thing: the real planner, the real statement, the real compiled
    /// expression tree.
    /// </remarks>
    [TestClass]
    public class CosmosToClrAsyncEnumerableConverterTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        /// <summary>
        /// An executor that answers with documents rather than with a service, and records what it was
        /// asked.
        /// </summary>
        sealed class StubExecutor : ICosmosQueryExecutor
        {

            readonly string[] _documents;

            public StubExecutor(params string[] documents)
            {
                _documents = documents;
            }

            public CosmosQuery? Executed { get; private set; }

            public async IAsyncEnumerable<JsonElement> ExecuteAsync(CosmosQuery query, PartitionKey? partitionKey = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                Executed = query;

                foreach (var document in _documents)
                {
                    // Yields on each row, so the plan above is exercised as a genuinely asynchronous
                    // sequence rather than one that happens to complete synchronously.
                    await Task.Yield();
                    yield return JsonDocument.Parse(document).RootElement.Clone();
                }
            }

        }

        /// <summary>
        /// The minimum a compiled plan needs to find its way back to the table it reads.
        /// </summary>
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

        CosmosTable _table = null!;
        StubExecutor _executor = null!;
        CalciteSchema _rootSchema = null!;
        JavaTypeFactoryImpl _typeFactory = null!;

        [TestInitialize]
        public void Initialize()
        {
            _typeFactory = new JavaTypeFactoryImpl();
            Given();
        }

        /// <summary>
        /// Exposes a table whose container holds the given documents.
        /// </summary>
        void Given(params string[] documents)
        {
            _executor = new StubExecutor(documents);
            Register(new CosmosTable(Products, _executor));
        }

        /// <summary>
        /// Exposes a table built from container metadata alone, with no client behind it.
        /// </summary>
        void GivenNoExecutor()
        {
            Register(new CosmosTable(Products));
        }

        void Register(CosmosTable table)
        {
            _table = table;
            _rootSchema = CalciteSchema.createRootSchema(false);
            _rootSchema.add("products", _table);
        }

        RelNode PlanLogical(string sql)
        {
            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                _rootSchema,
                java.util.Collections.emptyList(),
                _typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var validator = SqlValidatorUtil.newValidator(
                SqlStdOperatorTable.instance(), catalogReader, _typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(_typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).rel;
        }

        /// <summary>
        /// Plans a statement and asks for the best plan in the asynchronous convention, which is what a
        /// caller of this adapter must ask for.
        /// </summary>
        RelNode PlanToAsync(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <summary>
        /// Compiles a planned tree and reads every row it produces.
        /// </summary>
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

        // ── Planning ──────────────────────────────────────────────────────────────

        /// <remarks>
        /// Without the converter rule the pushed-down subtree is a statement nothing can read the rows
        /// of, and the planner has no complete plan to return.
        /// </remarks>
        [TestMethod]
        public void ThePlannerReachesTheAsynchronousConventionThroughTheConverter()
        {
            var plan = PlanToAsync("SELECT \"id\" FROM products AS c");

            plan.Should().BeOfType<CosmosToClrAsyncEnumerableConverter>();
            plan.getInput(0).getConvention().Should().BeSameAs(_table.Convention);
        }

        // ── Execution ─────────────────────────────────────────────────────────────

        /// <remarks>
        /// A one-column result is the value rather than a one-element row, which is what
        /// <c>ImplementRoot</c> arranges and what every caller of a query expects.
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadASingleColumnAsTheValueItself()
        {
            Given("""{ "id": "a" }""", """{ "id": "b" }""");

            var rows = await Execute(PlanToAsync("SELECT \"id\" FROM products AS c"));

            rows.Should().Equal("a", "b");
        }

        /// <remarks>
        /// The statement the stub is handed is the projected object constructor, not <c>SELECT VALUE c</c>.
        /// One row shape reaches the reader whatever the query did.
        /// </remarks>
        [TestMethod]
        public async Task ShouldProjectAnObjectKeyedByOutputFieldName()
        {
            Given("""{ "id": "a" }""");

            await Execute(PlanToAsync("SELECT \"id\" FROM products AS c"));

            _executor.Executed!.Value.Sql.Should().Contain("SELECT VALUE {").And.Contain("\"id\": c.id");
        }

        /// <remarks>
        /// Cosmos omits a property whose value is undefined, so a row is a subset of the output fields and
        /// their positions are not the fields' positions. Reading by name is what makes that survivable.
        /// </remarks>
        [TestMethod]
        public async Task ShouldReadAnAbsentPropertyAsNull()
        {
            Given("""{ "id": "a" }""", """{ }""");

            var rows = await Execute(PlanToAsync("SELECT \"id\" FROM products AS c"));

            rows.Should().Equal(new object[] { "a", null! });
        }

        [TestMethod]
        public async Task ShouldReadEveryColumnOfAWiderRow()
        {
            Given("""{ "id": "a", "_ts": 17 }""");

            var rows = await Execute(PlanToAsync("SELECT \"id\", \"_ts\" FROM products AS c"));

            rows.Should().HaveCount(1);
            ((object[])rows[0]).Should().Equal("a", java.lang.Long.valueOf(17L));
        }

        /// <remarks>
        /// A table built from container metadata alone plans identically and cannot be read from. This is
        /// the first point at which that could show, and it says so rather than throwing a null reference.
        /// </remarks>
        [TestMethod]
        public async Task ShouldRefuseToExecuteATableWithNoExecutor()
        {
            GivenNoExecutor();

            var plan = PlanToAsync("SELECT \"id\" FROM products AS c");

            var act = async () => await Execute(plan);
            (await act.Should().ThrowAsync<CosmosExecutionException>()).WithMessage("*has no query executor*");
        }

    }

}
