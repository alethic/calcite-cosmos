using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

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
    /// Whether a <c>DELETE</c> that empties one logical partition is planned as the single request
    /// the service offers, and — more importantly — whether it declines to be where the account
    /// cannot do it.
    /// </summary>
    /// <remarks>
    /// The capability is a per-account preview no environment here can enable, so the probe is
    /// stubbed on both answers. That is the point of the gate: the recovery must be invisible where
    /// the answer is no, and no test can reach an account where it is yes.
    /// </remarks>
    [TestClass]
    public class CosmosPartitionDeletePlanningTests
    {

        static CosmosContainerMetadata Container(bool supported) =>
            new CosmosContainerMetadata("products", new[] { "/category" }).WithPartitionKeyDeleteProbe(() => supported);

        static RelNode Plan(CosmosTable table, string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", table);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(rootSchema, java.util.Collections.emptyList(), typeFactory, new CalciteConnectionConfigImpl(properties));
            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseStmt();
            var validator = SqlValidatorUtil.newValidator(SqlStdOperatorTable.instance(), catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());
            var logical = converter.convertQuery(validator.validate(parsed), false, true).rel;

            foreach (var rule in CosmosRules.GetRules(table.Convention))
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

        /// <remarks>
        /// The predicate names the partition and nothing else, so every row the scan would have
        /// read is a row the service will remove — which is what makes the single request faithful
        /// rather than merely cheaper.
        /// </remarks>
        [TestMethod]
        public void APartitionKeyOnlyDeleteBecomesOneRequestWhereTheAccountAllowsIt()
        {
            var modify = Find<CosmosTableModify>(Plan(new CosmosTable(Container(supported: true)), "DELETE FROM products WHERE \"category\" = 'bikes'"));

            modify.Should().NotBeNull();
            modify!.Write.Should().Be(CosmosWriteOperation.DeletePartition);
        }

        /// <remarks>
        /// The gate, and the reason it exists: the operation is a per-account preview, and an
        /// account without it answers 400. Planning the fast path there would turn a working
        /// statement into a failing one, which is the one thing a pushdown must never do.
        /// </remarks>
        [TestMethod]
        public void TheSameDeleteStaysARowAtATimeWhereTheAccountCannot()
        {
            var modify = Find<CosmosTableModify>(Plan(new CosmosTable(Container(supported: false)), "DELETE FROM products WHERE \"category\" = 'bikes'"));

            modify.Should().NotBeNull();
            modify!.Write.Should().Be(CosmosWriteOperation.Delete);
        }

        /// <remarks>
        /// A residual conjunct means the statement asked for some of the partition, and the service
        /// would empty all of it — data loss rather than a slow plan.
        /// </remarks>
        [TestMethod]
        public void AResidualPredicateStaysARowAtATime()
        {
            var modify = Find<CosmosTableModify>(Plan(new CosmosTable(Container(supported: true)), "DELETE FROM products WHERE \"category\" = 'bikes' AND \"_ts\" > 5"));

            modify.Should().NotBeNull();
            modify!.Write.Should().Be(CosmosWriteOperation.Delete);
        }

        /// <remarks>
        /// An <c>id</c> alongside the key is a single-document delete, which the per-row path
        /// already does with a point operation.
        /// </remarks>
        [TestMethod]
        public void ADeleteNamingAnIdStaysARowAtATime()
        {
            var modify = Find<CosmosTableModify>(Plan(new CosmosTable(Container(supported: true)), "DELETE FROM products WHERE \"category\" = 'bikes' AND \"id\" = 'x'"));

            modify.Should().NotBeNull();
            modify!.Write.Should().Be(CosmosWriteOperation.Delete);
        }

        /// <remarks>
        /// The capability is asked at most once, and only where a statement could use it — a plan
        /// with no whole-partition delete in it must not pay for a round trip it cannot spend.
        /// </remarks>
        [TestMethod]
        public void TheCapabilityIsNotProbedForAStatementThatCannotUseIt()
        {
            var probes = 0;
            var container = new CosmosContainerMetadata("products", new[] { "/category" })
                .WithPartitionKeyDeleteProbe(() => { probes++; return true; });

            var table = new CosmosTable(container);

            Plan(table, "DELETE FROM products WHERE \"id\" = 'x'");
            probes.Should().Be(0, "the predicate does not pin the partition key, so the capability cannot matter");

            Plan(table, "DELETE FROM products WHERE \"category\" = 'bikes'");
            probes.Should().Be(1, "asked once for the statement that could use it");

            Plan(table, "DELETE FROM products WHERE \"category\" = 'shoes'");
            probes.Should().Be(1, "and remembered thereafter");
        }

    }

}
