using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.schema;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;
using org.apache.calcite.tools;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Drives the Cosmos relational nodes through <c>Implement</c> against a real
    /// <see cref="RelOptTable"/>, asserting the Cosmos SQL they produce.
    /// </summary>
    [TestClass]
    public class CosmosRelImplementTests
    {

        /// <remarks>
        /// The composite index is declared over <c>/id</c> and <c>/_ts</c> because collations
        /// address fields by ordinal, so only promoted columns can be sorted on. A path inside the
        /// map column would require an <c>ITEM</c> call, which a collation cannot express.
        /// </remarks>
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

        RelOptCluster _cluster = null!;
        RelOptTable _table = null!;
        RexBuilder _rex = null!;
        CosmosConvention _convention = null!;

        [TestInitialize]
        public void Initialize()
        {
            // A catalog reader resolves a Table into a RelOptTable without opening the internal
            // Calcite connection that RelBuilder.create would, which needs the JDBC driver.
            var typeFactory = new org.apache.calcite.jdbc.JavaTypeFactoryImpl();
            var rootSchema = org.apache.calcite.jdbc.CalciteSchema.createRootSchema(false);
            rootSchema.add("products", new CosmosTable(Products));

            var reader = new org.apache.calcite.prepare.CalciteCatalogReader(
                rootSchema,
                java.util.Collections.emptyList(),
                typeFactory,
                new org.apache.calcite.config.CalciteConnectionConfigImpl(new java.util.Properties()));

            _table = reader.getTable(java.util.Collections.singletonList("products"));

            var planner = new org.apache.calcite.plan.volcano.VolcanoPlanner();
            planner.addRelTraitDef(ConventionTraitDef.INSTANCE);

            _rex = new RexBuilder(typeFactory);
            _cluster = RelOptCluster.create(planner, _rex);
            _convention = CosmosConvention.Create(Products);
        }

        CosmosImplementor Implementor() => new(_rex, Products);

        RelTraitSet Traits() => _cluster.traitSetOf(_convention);

        CosmosTableScan Scan() => new(_cluster, Traits(), _table);

        RexNode Ref(int index) => _rex.makeInputRef(_table.getRowType().getFieldList().size() > index
            ? ((org.apache.calcite.rel.type.RelDataTypeField)_table.getRowType().getFieldList().get(index)).getType()
            : _cluster.getTypeFactory().createSqlType(SqlTypeName.ANY), index);

        RexNode Str(string value) => _rex.makeLiteral(value, _cluster.getTypeFactory().createSqlType(SqlTypeName.VARCHAR, value.Length));

        static string Sql(CosmosRel rel, CosmosImplementor implementor)
        {
            rel.Implement(implementor);
            return implementor.Build().Sql;
        }

        // ── Row type ──────────────────────────────────────────────────────────────

        [TestMethod]
        public void RowTypeIsTheMapColumnPlusPromotedColumns()
        {
            var names = new List<string>();
            var fields = _table.getRowType().getFieldList();
            for (var i = 0; i < fields.size(); i++)
                names.Add(((org.apache.calcite.rel.type.RelDataTypeField)fields.get(i)).getName());

            names.Should().Equal("_MAP", "id", "_ts", "_etag", "category");
        }

        /// <remarks>
        /// A partition key of <c>/id</c> must not promote <c>id</c> a second time.
        /// </remarks>
        [TestMethod]
        public void PartitionKeyOnIdDoesNotDuplicateTheColumn()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("c", new[] { "/id" }));
            table.GetPromotedColumnNames().Should().Equal("id", "_ts", "_etag");
        }

        /// <remarks>
        /// A nested declared path has no column name under the current name-based binding, so it
        /// stays in the map column rather than being promoted incorrectly.
        /// </remarks>
        [TestMethod]
        public void NestedPartitionKeyIsNotPromoted()
        {
            var table = new CosmosTable(new CosmosContainerMetadata("c", new[] { "/inventory/sku" }));
            table.GetPromotedColumnNames().Should().Equal("id", "_ts", "_etag");
        }

        // ── Scan ──────────────────────────────────────────────────────────────────

        [TestMethod]
        public void ScanSelectsTheDocument()
        {
            Sql(Scan(), Implementor()).Should().Be("SELECT VALUE c FROM products c");
        }

        [TestMethod]
        public void ScanBindsMapColumnToRootAndPromotedColumnsToProperties()
        {
            var implementor = Implementor();
            Scan().Implement(implementor);

            implementor.Fields[0].ToString().Should().Be("c");
            implementor.Fields[1].ToString().Should().Be("c.id");
            implementor.Fields[4].ToString().Should().Be("c.category");
        }

        // ── Filter ────────────────────────────────────────────────────────────────

        [TestMethod]
        public void FilterRendersAWhereClause()
        {
            var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1), Str("abc")));

            var implementor = Implementor();
            Sql(filter, implementor).Should().Be("SELECT VALUE c FROM products c WHERE (c.id = @p0)");
            implementor.Build().Parameters.Should().ContainSingle().Which.Value.Should().Be("abc");
        }

        [TestMethod]
        public void FilterOverAMapPropertyRendersAPath()
        {
            var item = _rex.makeCall(SqlStdOperatorTable.ITEM, Ref(0), Str("city"));
            var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, item, Str("Seattle")));

            Sql(filter, Implementor()).Should().Be("SELECT VALUE c FROM products c WHERE (c.city = @p0)");
        }

        /// <remarks>
        /// Stacked filters are normally merged by the planner; conjoining defensively ensures
        /// neither predicate is silently dropped if they are not.
        /// </remarks>
        [TestMethod]
        public void StackedFiltersAreConjoined()
        {
            var inner = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1), Str("a")));
            var outer = new CosmosFilter(_cluster, Traits(), inner,
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(1), Str("b")));

            Sql(outer, Implementor()).Should().Be("SELECT VALUE c FROM products c WHERE ((c.id = @p0) AND (c.id = @p1))");
        }

        [TestMethod]
        public void UntranslatableFilterIsRefused()
        {
            var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.IS_NOT_NULL, _rex.makeCall(SqlStdOperatorTable.UPPER, Ref(1))));

            var act = () => Sql(filter, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

        // ── Sort ──────────────────────────────────────────────────────────────────

        /// <remarks>
        /// Null placement is stated explicitly as UNSPECIFIED. Calcite's defaults conflict with
        /// Cosmos's ordering; that interaction is covered in <c>CosmosSortResolutionTests</c>.
        /// </remarks>
        static RelCollation Collation(params (int Index, RelFieldCollation.Direction Direction)[] keys)
        {
            var list = new java.util.ArrayList();
            foreach (var (index, direction) in keys)
                list.add(new RelFieldCollation(index, direction, RelFieldCollation.NullDirection.UNSPECIFIED));

            return RelCollations.of(list);
        }

        CosmosSort SortOver(RelNode input, RelCollation collation, RexNode? offset = null, RexNode? fetch = null) =>
            new(_cluster, Traits(), input, collation, offset, fetch);

        [TestMethod]
        public void SingleKeySortRendersOrderBy()
        {
            var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));
            Sql(sort, Implementor()).Should().Be("SELECT VALUE c FROM products c ORDER BY c.id ASC");
        }

        [TestMethod]
        public void DescendingSortRendersDesc()
        {
            var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.DESCENDING)));
            Sql(sort, Implementor()).Should().EndWith("ORDER BY c.id DESC");
        }

        [TestMethod]
        public void SortOverFilterCombinesBothClauses()
        {
            var filter = new CosmosFilter(_cluster, Traits(), Scan(),
                _rex.makeCall(SqlStdOperatorTable.EQUALS, Ref(4), Str("bikes")));
            var sort = SortOver(filter, Collation((1, RelFieldCollation.Direction.ASCENDING)));

            Sql(sort, Implementor()).Should().Be("SELECT VALUE c FROM products c WHERE (c.category = @p0) ORDER BY c.id ASC");
        }

        [TestMethod]
        public void OffsetAndFetchRenderAsOffsetLimit()
        {
            var sort = SortOver(
                Scan(),
                Collation((1, RelFieldCollation.Direction.ASCENDING)),
                _rex.makeExactLiteral(new java.math.BigDecimal(5)),
                _rex.makeExactLiteral(new java.math.BigDecimal(10)));

            Sql(sort, Implementor()).Should().EndWith("ORDER BY c.id ASC OFFSET 5 LIMIT 10");
        }

        /// <remarks>
        /// The container declares a composite index over (/id, /_ts), which are fields 1 and 2.
        /// </remarks>
        [TestMethod]
        public void MultiKeySortWithAMatchingCompositeIndexIsAccepted()
        {
            var sort = SortOver(Scan(), Collation(
                (1, RelFieldCollation.Direction.ASCENDING),
                (2, RelFieldCollation.Direction.ASCENDING)));

            Sql(sort, Implementor()).Should().EndWith("ORDER BY c.id ASC, c._ts ASC");
        }

        /// <remarks>
        /// A composite index also serves the fully inverted sort.
        /// </remarks>
        [TestMethod]
        public void FullyInvertedMultiKeySortIsAccepted()
        {
            var sort = SortOver(Scan(), Collation(
                (1, RelFieldCollation.Direction.DESCENDING),
                (2, RelFieldCollation.Direction.DESCENDING)));

            Sql(sort, Implementor()).Should().EndWith("ORDER BY c.id DESC, c._ts DESC");
        }

        [TestMethod]
        public void PartiallyInvertedMultiKeySortIsRefused()
        {
            var sort = SortOver(Scan(), Collation(
                (1, RelFieldCollation.Direction.ASCENDING),
                (2, RelFieldCollation.Direction.DESCENDING)));

            var act = () => Sql(sort, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*composite index*");
        }

        /// <remarks>
        /// Without a matching composite index the service rejects the query outright, so pushing
        /// it down would be a defect rather than a pessimisation.
        /// </remarks>
        [TestMethod]
        public void MultiKeySortWithoutACompositeIndexIsRefused()
        {
            var sort = SortOver(Scan(), Collation(
                (1, RelFieldCollation.Direction.ASCENDING),
                (4, RelFieldCollation.Direction.DESCENDING)));

            var act = () => Sql(sort, Implementor());
            act.Should().Throw<CosmosTranslationException>().WithMessage("*composite index*");
        }

        [TestMethod]
        public void StackedSortsAreRefused()
        {
            var inner = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));
            var outer = SortOver(inner, Collation((2, RelFieldCollation.Direction.ASCENDING)));

            var act = () => Sql(outer, Implementor());
            act.Should().Throw<CosmosTranslationException>();
        }

        [TestMethod]
        public void SortIsRefusedWhenGroupingIsPresent()
        {
            var implementor = Implementor();
            implementor.Query.AddGroupBy("c.category");

            var sort = SortOver(Scan(), Collation((1, RelFieldCollation.Direction.ASCENDING)));

            var act = () => Sql(sort, implementor);
            act.Should().Throw<CosmosTranslationException>().WithMessage("*GROUP BY*");
        }

        // ── Convention boundary ───────────────────────────────────────────────────

        [TestMethod]
        public void VisitingANonCosmosNodeIsRefused()
        {
            var logical = org.apache.calcite.rel.logical.LogicalTableScan.create(_cluster, _table, java.util.Collections.emptyList());

            var act = () => Implementor().Visit(logical);
            act.Should().Throw<CosmosTranslationException>();
        }

    }

}
