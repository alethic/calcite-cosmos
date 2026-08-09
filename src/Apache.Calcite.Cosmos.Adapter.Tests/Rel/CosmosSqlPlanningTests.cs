using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.avatica.util;
using org.apache.calcite.config;
using org.apache.calcite.jdbc;
using org.apache.calcite.plan;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Plans SQL text into relational algebra so that the trees the conversion rules must match can
    /// be observed rather than assumed.
    /// </summary>
    /// <remarks>
    /// Calcite's usual entry points for this — <c>RelBuilder.create</c> and
    /// <c>Frameworks.getPlanner</c> — open an internal JDBC connection, which fails under IKVM with
    /// <c>ClassNotFoundException: org.apache.calcite.jdbc.CalciteJdbc41Factory</c>. The parser,
    /// validator, and converter do not need one, so they are wired up directly here.
    /// </remarks>
    [TestClass]
    public class CosmosSqlPlanningTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        static string PlanText(string sql) => RelOptUtil.toString(Plan(sql)).Trim().Replace("\r\n", "\n");

        static RelNode Plan(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", new CosmosTable(Products));

            // Identifiers are matched as written: container and property names are case-sensitive
            // in Cosmos, and the promoted columns are lower case.
            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");
            var connectionConfig = new CalciteConnectionConfigImpl(properties);

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, connectionConfig);

            var parserConfig = SqlParser.config().withUnquotedCasing(Casing.UNCHANGED);
            var parsed = SqlParser.create(sql, parserConfig).parseQuery();

            var validator = SqlValidatorUtil.newValidator(
                SqlStdOperatorTable.instance(),
                catalogReader,
                typeFactory,
                SqlValidator.Config.DEFAULT);

            var validated = validator.validate(parsed);

            var planner = new org.apache.calcite.plan.volcano.VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));

            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validated, false, true).rel;
        }

        const string UnnestSql = "SELECT c.\"id\" FROM products AS c, UNNEST(c.\"_MAP\"['tags']) AS t";

        /// <summary>
        /// Finds the first correlate in a plan.
        /// </summary>
        static Correlate? FindCorrelate(RelNode node)
        {
            if (node is Correlate correlate)
                return correlate;

            var inputs = node.getInputs();
            for (var i = 0; i < inputs.size(); i++)
                if (FindCorrelate((RelNode)inputs.get(i)) is Correlate found)
                    return found;

            return null;
        }

        /// <remarks>
        /// Confirms the table's <c>toRel</c> is reached from SQL, so a scan arrives already in the
        /// Cosmos convention.
        /// </remarks>
        [TestMethod]
        public void ScanIsProducedByTheTable()
        {
            PlanText("SELECT * FROM products").Should().Be(
                "LogicalProject(_MAP=[$0], id=[$1], _ts=[$2], _etag=[$3], category=[$4])\n" +
                "  CosmosTableScan(table=[[products]])");
        }

        [TestMethod]
        public void FilterPlansOverTheScan()
        {
            PlanText("SELECT * FROM products AS c WHERE c.\"id\" = 'x'").Should().Contain("LogicalFilter(condition=[=($1, 'x')])");
        }

        /// <remarks>
        /// The shape <see cref="CosmosUnnestRule"/> matches, observed rather than assumed.
        /// </remarks>
        [TestMethod]
        public void UnnestPlansToACorrelateOverUncollect()
        {
            var plan = PlanText(UnnestSql);

            plan.Should().Contain("LogicalCorrelate(correlation=[$cor0], joinType=[inner]");
            plan.Should().Contain("Uncollect");
            plan.Should().Contain("LogicalProject(EXPR$0=[ITEM($cor0._MAP, 'tags')])");
        }

        /// <summary>
        /// The rule's shape recognition, driven from real planner output.
        /// </summary>
        [TestMethod]
        public void UnnestRuleRecognisesThePlannedCorrelate()
        {
            var correlate = FindCorrelate(Plan(UnnestSql));
            correlate.Should().NotBeNull();

            CosmosUnnestRule.GetArrayExpression(correlate!).Should().NotBeNull();
        }

        /// <summary>
        /// The extracted array expression resolves to the document path the traversal will emit.
        /// </summary>
        [TestMethod]
        public void PlannedArrayExpressionResolvesToAPath()
        {
            var correlate = FindCorrelate(Plan(UnnestSql))!;
            var array = CosmosUnnestRule.GetArrayExpression(correlate)!;

            var fields = CosmosImplementor.BindFields(correlate.getLeft().getRowType());
            // The correlate's own id, which is what makes this variable stand for the row being
            // scanned rather than the other side of a join.
            var translator = new CosmosRexTranslator(correlate.getCluster().getRexBuilder(), fields, new CosmosParameterList(), correlate.getCorrelationId());

            translator.TryResolvePath(array, out var path).Should().BeTrue();
            path!.ToString().Should().Be("c.tags");

            // And without that declaration it does not resolve at all.
            var unaware = new CosmosRexTranslator(correlate.getCluster().getRexBuilder(), fields, new CosmosParameterList());
            unaware.TryResolvePath(array, out _).Should().BeFalse();
        }

    }

}
