using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;

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

namespace Apache.Calcite.Cosmos.Adapter.Tests.Sql
{

    /// <summary>
    /// How the Cosmos-specific functions are made available to a query, and what happens where one
    /// cannot be pushed down.
    /// </summary>
    /// <remarks>
    /// These functions have no in-process implementation, on purpose: they exist to be rendered into a
    /// statement the service evaluates. What that costs, and whether it should be paid differently, is
    /// what these measure.
    /// </remarks>
    [TestClass]
    public class CosmosFunctionResolutionTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        CosmosTable _table = null!;

        [TestInitialize]
        public void Initialize()
        {
            _table = new CosmosTable(Products);
        }

        RelNode PlanToAsync(string sql, bool withOperatorTable = true)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseQuery();

            var operators = withOperatorTable
                ? org.apache.calcite.sql.util.SqlOperatorTables.chain(SqlStdOperatorTable.instance(), Adapter.Sql.CosmosOperators.Instance)
                : SqlStdOperatorTable.instance();

            var validator = SqlValidatorUtil.newValidator(operators, catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).project();

            foreach (var rule in CosmosRules.GetRules(_table.Convention))
                planner.addRule(rule);

            var desired = logical.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance).simplify();
            planner.setRoot(planner.changeTraits(logical, desired));

            return planner.findBestExp();
        }

        /// <remarks>
        /// The seam a caller wires today, and the reason the README says to.
        /// </remarks>
        [TestMethod]
        public void AFunctionResolvesOnlyWhenTheOperatorTableIsChained()
        {
            var withTable = () => PlanToAsync("SELECT * FROM products AS c WHERE IS_DEFINED(c.\"category\")");
            withTable.Should().NotThrow();

            var without = () => PlanToAsync("SELECT * FROM products AS c WHERE IS_DEFINED(c.\"category\")", withOperatorTable: false);
            without.Should().Throw<Exception>("the standard table has nothing to resolve IS_DEFINED to");
        }

        /// <summary>
        /// A Cosmos function that cannot be pushed leaves the query with no plan at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// It is applied here to a computed projection, which has no document path for a statement to
        /// name, so nothing can render it and nothing can evaluate it either.
        /// </para>
        /// <para>
        /// The failure is at plan time, which is the better of the two available failures — the
        /// alternative, registering these as schema functions bound to CLR methods, would put a body
        /// behind them and turn this into an exception once rows were already flowing.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void AFunctionThatCannotBePushedFailsWhilePlanning()
        {
            var plan = () => PlanToAsync("SELECT IS_DEFINED(t.x) FROM (SELECT INITCAP(c.\"_etag\") AS x FROM products AS c) AS t");

            plan.Should().Throw<Exception>("there is no in-process implementation, and that is deliberate");
        }

    }

}
