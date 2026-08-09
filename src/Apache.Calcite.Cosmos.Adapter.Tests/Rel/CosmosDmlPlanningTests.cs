using System;

using Apache.Calcite.Cosmos.Adapter.Client;
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
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.parser;
using org.apache.calcite.sql.validate;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Covers how a write reaches the adapter: what the validator permits, what the planner produces,
    /// and which of it is selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same direct wiring as <see cref="CosmosSqlPlanningTests"/>, for the same reason — Calcite's
    /// usual entry points open an internal JDBC connection, which fails under IKVM — with
    /// <c>parseStmt</c> in place of <c>parseQuery</c>, DML not being a query.
    /// </para>
    /// <para>
    /// The plan text is asserted exactly, because the shapes here were observed rather than derived and
    /// several of them are load-bearing: that the modify's input is always the table's whole row type,
    /// that an unmentioned column arrives as a typed null, and that an <c>UPDATE</c> hands over its
    /// <c>SET</c> list separately.
    /// </para>
    /// </remarks>
    [TestClass]
    public class CosmosDmlPlanningTests
    {

        static readonly CosmosContainerMetadata Products = new("products", new[] { "/category" });

        static readonly CosmosContainerMetadata Archive = new("archive", new[] { "/category" });

        CosmosTable _products = null!;
        CosmosTable _archive = null!;

        [TestInitialize]
        public void Initialize()
        {
            _products = new CosmosTable(Products);
            _archive = new CosmosTable(Archive);
        }

        string PlanText(string sql) => RelOptUtil.toString(PlanLogical(sql)).Trim().Replace("\r\n", "\n");

        RelNode PlanLogical(string sql)
        {
            var typeFactory = new JavaTypeFactoryImpl();

            var rootSchema = CalciteSchema.createRootSchema(false);
            rootSchema.add("products", _products);
            rootSchema.add("archive", _archive);

            var properties = new java.util.Properties();
            properties.setProperty("caseSensitive", "true");

            var catalogReader = new CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new CalciteConnectionConfigImpl(properties));

            var parsed = SqlParser.create(sql, SqlParser.config().withUnquotedCasing(Casing.UNCHANGED)).parseStmt();

            var validator = SqlValidatorUtil.newValidator(
                SqlStdOperatorTable.instance(), catalogReader, typeFactory, SqlValidator.Config.DEFAULT);

            var planner = new VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            // As in the lookup join's tests: the CLR join rules ask their inputs for a collation and
            // fail where the trait is not registered, whether or not anything produces one.
            planner.addRelTraitDef(RelCollationTraitDef.INSTANCE);

            var cluster = RelOptCluster.create(planner, new RexBuilder(typeFactory));
            var converter = new SqlToRelConverter(null, validator, catalogReader, cluster, StandardConvertletTable.INSTANCE, SqlToRelConverter.config());

            return converter.convertQuery(validator.validate(parsed), false, true).rel;
        }

        /// <summary>
        /// Plans a statement for the asynchronous convention, with the adapter's rules and the CLR ones.
        /// </summary>
        RelNode Plan(string sql)
        {
            var logical = PlanLogical(sql);
            var planner = (VolcanoPlanner)logical.getCluster().getPlanner();

            foreach (var rule in CosmosRules.GetRules(_products.Convention))
                planner.addRule(rule);

            foreach (var rule in CosmosRules.GetRules(_archive.Convention))
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

        // ---- what the validator permits -------------------------------------------------------

        /// <summary>
        /// The reason <see cref="CosmosColumnStrategies"/> exists, pinned as the behaviour it produces.
        /// </summary>
        /// <remarks>
        /// Without the strategies this statement does not reach a rule at all: <c>_MAP</c> and <c>id</c>
        /// are both declared <c>NOT NULL</c>, and the validator refuses an insert that omits a column
        /// which is neither nullable nor defaulted.
        /// </remarks>
        [TestMethod]
        public void ColumnsMayBeOmittedFromAnInsert()
        {
            PlanText("INSERT INTO products (\"id\", \"category\") VALUES ('1', 'books')").Should().Be(
                "LogicalTableModify(table=[[products]], operation=[INSERT], flattened=[false])\n" +
                "  LogicalProject(_MAP=[null:(VARCHAR NOT NULL, ANY NOT NULL) MAP], id=[$0], _ts=[null:BIGINT], _etag=[null:VARCHAR], category=[$1])\n" +
                "    LogicalValues(tuples=[[{ '1', 'books' }]])");
        }

        /// <summary>
        /// An unmentioned column reaches the write as a null, which is what makes the "contributes only
        /// when not null" rule necessary rather than merely convenient.
        /// </summary>
        [TestMethod]
        public void AnUnmentionedColumnArrivesAsANull()
        {
            PlanText("INSERT INTO products (\"_MAP\") SELECT \"_MAP\" FROM archive").Should().Be(
                "LogicalTableModify(table=[[products]], operation=[INSERT], flattened=[false])\n" +
                "  LogicalProject(_MAP=[$0], id=[null:VARCHAR], _ts=[null:BIGINT], _etag=[null:VARCHAR], category=[null:ANY])\n" +
                "    CosmosTableScan(table=[[archive]])");
        }

        /// <summary>
        /// The service's own properties cannot be written, and the validator is what says so.
        /// </summary>
        [TestMethod]
        public void ServiceMaintainedColumnsCannotBeInserted()
        {
            var act = () => PlanText("INSERT INTO products (\"id\", \"_ts\") VALUES ('1', 5)");

            act.Should().Throw<java.lang.Throwable>()
                .Where(e => e.ToString().Contains("Cannot INSERT into generated column '_ts'"));
        }

        /// <summary>
        /// With <c>_ts</c> and <c>_etag</c> excluded, a column list may be omitted and the remaining
        /// three columns supplied positionally.
        /// </summary>
        [TestMethod]
        public void AnInsertWithoutAColumnListSuppliesTheWritableColumns()
        {
            PlanText("INSERT INTO products SELECT \"_MAP\", \"id\", \"category\" FROM archive").Should().Be(
                "LogicalTableModify(table=[[products]], operation=[INSERT], flattened=[false])\n" +
                "  LogicalProject(_MAP=[$0], id=[$1], _ts=[null:BIGINT], _etag=[null:VARCHAR], category=[$4])\n" +
                "    CosmosTableScan(table=[[archive]])");
        }

        /// <summary>
        /// A map literal cannot be inserted, and the limitation is Calcite's rather than the adapter's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The validator's implicit coercion casts the source row to the target row type, and building a
        /// <c>SqlDataTypeSpec</c> for <c>MAP&lt;VARCHAR, ANY&gt;</c> is unimplemented —
        /// <c>SqlTypeUtil.convertTypeToSpec</c> throws on the <c>ANY</c>. An explicit <c>CAST</c> fails
        /// the same way, for the same reason.
        /// </para>
        /// <para>
        /// Recorded rather than worked around, because the shape that does work is the more useful one:
        /// a source column already typed <c>MAP&lt;VARCHAR, ANY&gt;</c> needs no coercion, and that is
        /// what a scan of another container supplies. See
        /// <see cref="AnUnmentionedColumnArrivesAsANull"/>, which inserts exactly that.
        /// </para>
        /// </remarks>
        [TestMethod]
        public void AMapLiteralCannotBeInserted()
        {
            var act = () => PlanText("INSERT INTO products (\"_MAP\") VALUES (MAP['id', 'x'])");

            act.Should().Throw<java.lang.Throwable>()
                .Where(e => e.ToString().Contains("convertTypeToSpec: ANY"));
        }

        // ---- the shapes a rule must match ------------------------------------------------------

        [TestMethod]
        public void DeletePlansToAModifyOverTheRowsToDelete()
        {
            PlanText("DELETE FROM products WHERE \"id\" = 'x'").Should().Be(
                "LogicalTableModify(table=[[products]], operation=[DELETE], flattened=[false])\n" +
                "  LogicalProject(_MAP=[$0], id=[$1], _ts=[$2], _etag=[$3], category=[$4])\n" +
                "    LogicalFilter(condition=[=($1, 'x')])\n" +
                "      CosmosTableScan(table=[[products]])");
        }

        /// <summary>
        /// What an <c>UPDATE</c> would have to work from, recorded against the day it is implemented.
        /// </summary>
        /// <remarks>
        /// The named columns and their new values arrive separately from the rows, which is exactly the
        /// input <c>PatchItemAsync</c> takes. Nothing consumes this yet — <see cref="UpdateIsDeclined"/>
        /// is the current behaviour — but the shape is what makes patch the obvious implementation
        /// rather than a read-modify-write.
        /// </remarks>
        [TestMethod]
        public void UpdateCarriesItsSetListSeparatelyFromTheRows()
        {
            PlanText("UPDATE products SET \"category\" = 'x' WHERE \"id\" = 'y'").Should().Be(
                "LogicalTableModify(table=[[products]], operation=[UPDATE], updateColumnList=[[category]], sourceExpressionList=[[$5]], flattened=[false])\n" +
                "  LogicalProject(_MAP=[$0], id=[$1], _ts=[$2], _etag=[$3], category=[$4], EXPR$0=['x'])\n" +
                "    LogicalFilter(condition=[=($1, 'y')])\n" +
                "      CosmosTableScan(table=[[products]])");
        }

        // ---- what the planner selects ----------------------------------------------------------

        /// <summary>
        /// An <c>INSERT</c> from <c>VALUES</c>, which is the shape that found the collation bug.
        /// </summary>
        /// <remarks>
        /// A <c>Values</c> node advertises several collations at once — every ordering a single row
        /// trivially satisfies — and a trait set holding more than one value for a trait throws when
        /// asked for its single value. The rule simplifies the input's traits for that reason. A
        /// <c>DELETE</c> never showed it: its input is a scan, which claims no collation at all.
        /// </remarks>
        [TestMethod]
        public void InsertFromValuesIsSelectedByThePlanner()
        {
            var modify = Find<CosmosTableModify>(Plan("INSERT INTO products (\"id\", \"category\") VALUES ('1', 'books')"));

            modify.Should().NotBeNull();
            modify!.Write.Should().Be(CosmosWriteOperation.Insert);
        }

        [TestMethod]
        public void DeleteIsSelectedByThePlanner()
        {
            var modify = Find<CosmosTableModify>(Plan("DELETE FROM products WHERE \"id\" = 'x'"));

            modify.Should().NotBeNull();
            modify!.Write.Should().Be(CosmosWriteOperation.Delete);
        }

        /// <summary>
        /// A second container's rules must not displace the first's.
        /// </summary>
        /// <remarks>
        /// This is the case the rule was originally written wrongly for. A converter rule's description
        /// comes from the traits it converts between, and this one names no container, so one instance
        /// per container gives several rules with the same description — of which a planner keeps one.
        /// Both containers are registered in <see cref="Plan"/>, so a rule bound to either would fail
        /// this for the other.
        /// </remarks>
        [TestMethod]
        public void EitherContainerCanBeWrittenTo()
        {
            Find<CosmosTableModify>(Plan("INSERT INTO products (\"id\", \"category\") VALUES ('1', 'books')")).Should().NotBeNull();
            Find<CosmosTableModify>(Plan("INSERT INTO archive (\"id\", \"category\") VALUES ('1', 'books')")).Should().NotBeNull();
        }

        /// <summary>
        /// An <c>UPDATE</c> has no implementation, and the plan fails rather than doing something else.
        /// </summary>
        /// <remarks>
        /// The right failure for a statement the adapter cannot carry out. A replace standing in for a
        /// patch would work and would silently cost a read-modify-write per row, which is the kind of
        /// substitution that is only discovered on a bill.
        /// </remarks>
        [TestMethod]
        public void UpdateIsDeclined()
        {
            var act = () => Plan("UPDATE products SET \"category\" = 'x' WHERE \"id\" = 'y'");

            act.Should().Throw<java.lang.Throwable>();
        }

        /// <summary>
        /// The regression the column strategies nearly caused, and which the row type does not show.
        /// </summary>
        /// <remarks>
        /// Declaring <c>_ts</c> and <c>_etag</c> <c>VIRTUAL</c> also refuses a write, and additionally
        /// tells Calcite they are not stored: <c>RelOptTableImpl.toRel</c> then drops them from the scan
        /// and projects a literal null, so every read of either returns nothing. The row type is
        /// identical either way, which is why this asserts the plan rather than the type.
        /// </remarks>
        [TestMethod]
        public void ServiceMaintainedColumnsAreStillRead()
        {
            var plan = PlanText("SELECT \"_ts\", \"_etag\" FROM products");

            plan.Should().Contain("_ts=[$2]");
            plan.Should().Contain("_etag=[$3]");
            plan.Should().NotContain("null:BIGINT");
        }

    }

}
