using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using org.apache.calcite.jdbc;
using org.apache.calcite.rel;
using org.apache.calcite.rex;
using org.apache.calcite.sql.fun;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Rel
{

    /// <summary>
    /// Covers the pure resolution logic the Rel nodes and their rules share. The nodes themselves
    /// require a table and schema layer that does not yet exist.
    /// </summary>
    [TestClass]
    public class CosmosSortResolutionTests
    {

        readonly JavaTypeFactoryImpl _types = new();
        readonly RexBuilder _rex;

        public CosmosSortResolutionTests()
        {
            _rex = new RexBuilder(_types);
        }

        static readonly CosmosPath[] Fields =
        {
            CosmosPath.Root("c"),                                                   // 0 — the map column
            CosmosPath.Root("c").Property("name"),                                  // 1
            CosmosPath.Root("c").Property("inventory").Property("quantity"),        // 2
        };

        static RelCollation Collation(params (int Index, RelFieldCollation.Direction Direction)[] keys)
        {
            var list = new java.util.ArrayList();
            foreach (var (index, direction) in keys)
                list.add(new RelFieldCollation(index, direction));

            return RelCollations.of(list);
        }

        // ── Field binding ─────────────────────────────────────────────────────────

        [TestMethod]
        public void MapColumnBindsToTheDocumentRoot()
        {
            var rowType = _types.builder()
                .add(CosmosImplementor.MapColumnName, SqlTypeName.ANY)
                .build();

            CosmosImplementor.BindFields(rowType).Should().ContainSingle().Which.ToString().Should().Be("c");
        }

        [TestMethod]
        public void PromotedColumnsBindToTheirProperties()
        {
            var rowType = _types.builder()
                .add(CosmosImplementor.MapColumnName, SqlTypeName.ANY)
                .add("id", SqlTypeName.VARCHAR)
                .add("_ts", SqlTypeName.BIGINT)
                .build();

            var fields = CosmosImplementor.BindFields(rowType);

            fields[0].ToString().Should().Be("c");
            fields[1].ToString().Should().Be("c.id");
            fields[2].ToString().Should().Be("c._ts");
        }

        [TestMethod]
        public void BindingHonoursTheRootAlias()
        {
            var rowType = _types.builder().add("id", SqlTypeName.VARCHAR).build();
            CosmosImplementor.BindFields(rowType, "p")[0].ToString().Should().Be("p.id");
        }

        // ── Path resolution ───────────────────────────────────────────────────────

        CosmosRexTranslator Translator() => new(_rex, Fields, new CosmosParameterList());

        [TestMethod]
        public void FieldReferenceResolvesToAPath()
        {
            Translator().TryResolvePath(_rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 1), out var path).Should().BeTrue();
            path!.ToString().Should().Be("c.name");
        }

        [TestMethod]
        public void ItemChainResolvesToAPath()
        {
            var map = _rex.makeInputRef(_types.createSqlType(SqlTypeName.ANY), 0);
            var inner = _rex.makeCall(SqlStdOperatorTable.ITEM, map, _rex.makeLiteral("address", _types.createSqlType(SqlTypeName.VARCHAR, 7)));
            var outer = _rex.makeCall(SqlStdOperatorTable.ITEM, inner, _rex.makeLiteral("city", _types.createSqlType(SqlTypeName.VARCHAR, 4)));

            Translator().TryResolvePath(outer, out var path).Should().BeTrue();
            path!.ToString().Should().Be("c.address.city");
        }

        [TestMethod]
        public void ComputedExpressionDoesNotResolveToAPath()
        {
            var node = _rex.makeCall(
                SqlStdOperatorTable.UPPER,
                _rex.makeInputRef(_types.createSqlType(SqlTypeName.VARCHAR), 1));

            Translator().TryResolvePath(node, out var path).Should().BeFalse();
            path.Should().BeNull();
        }

        // ── Sort key resolution ───────────────────────────────────────────────────

        [TestMethod]
        public void SortKeysResolveToPolicyPaths()
        {
            CosmosSort.TryResolveSortKeys(
                Collation((1, RelFieldCollation.Direction.ASCENDING), (2, RelFieldCollation.Direction.DESCENDING)),
                Fields,
                out var keys,
                out var paths).Should().BeTrue();

            keys.Should().SatisfyRespectively(
                k => { k.Path.Should().Be("/name"); k.Descending.Should().BeFalse(); },
                k => { k.Path.Should().Be("/inventory/quantity"); k.Descending.Should().BeTrue(); });

            paths[1].ToString().Should().Be("c.inventory.quantity");
        }

        [TestMethod]
        public void StrictDirectionsAreTreatedAsPlainDirections()
        {
            CosmosSort.TryResolveSortKeys(
                Collation((1, RelFieldCollation.Direction.STRICTLY_DESCENDING)),
                Fields,
                out var keys,
                out _).Should().BeTrue();

            keys[0].Descending.Should().BeTrue();
        }

        /// <remarks>
        /// Cosmos ORDER BY offers only ASC and DESC; a clustered collation has no equivalent.
        /// </remarks>
        [TestMethod]
        public void ClusteredCollationIsRefused()
        {
            CosmosSort.TryResolveSortKeys(
                Collation((1, RelFieldCollation.Direction.CLUSTERED)),
                Fields,
                out _,
                out _).Should().BeFalse();
        }

        [TestMethod]
        public void OutOfRangeFieldIsRefused()
        {
            CosmosSort.TryResolveSortKeys(
                Collation((9, RelFieldCollation.Direction.ASCENDING)),
                Fields,
                out _,
                out _).Should().BeFalse();
        }

        [TestMethod]
        public void EmptyCollationResolvesToNoKeys()
        {
            CosmosSort.TryResolveSortKeys(RelCollations.EMPTY, Fields, out var keys, out _).Should().BeTrue();
            keys.Should().BeEmpty();
        }

    }

}
