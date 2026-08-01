using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.schema.impl;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A Cosmos container exposed to Calcite as a table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The row type is one map column carrying the whole document, plus promoted scalar columns
    /// for paths the service guarantees or the container declares. A Cosmos query returns exactly
    /// one JSON value per row, so the map column is the faithful representation; the promoted
    /// columns exist so that planner metadata expressed over field ordinals — keys, collations,
    /// distribution — has something to refer to.
    /// </para>
    /// <para>
    /// Nothing here is inferred from sampling documents. Only <c>id</c>, the system properties,
    /// and single-segment declared paths are promoted. A nested partition key path such as
    /// <c>/inventory/sku</c> is not promotable under the current name-based binding and is left to
    /// the map column.
    /// </para>
    /// </remarks>
    public class CosmosTable : AbstractTable, TranslatableTable
    {

        readonly CosmosContainerMetadata _container;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container this table exposes.</param>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public CosmosTable(CosmosContainerMetadata container)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
        }

        /// <summary>
        /// Gets the container this table exposes.
        /// </summary>
        public CosmosContainerMetadata Container => _container;

        /// <summary>
        /// Returns the names of the columns promoted alongside the map column, in order.
        /// </summary>
        /// <remarks>
        /// Only declared or service-guaranteed paths qualify. Duplicates are suppressed so that a
        /// partition key of <c>/id</c> does not promote <c>id</c> twice.
        /// </remarks>
        /// <returns>The promoted column names.</returns>
        public IReadOnlyList<string> GetPromotedColumnNames()
        {
            var names = new List<string>
            {
                CosmosContainerMetadata.IdPropertyName,
                CosmosContainerMetadata.TimestampPropertyName,
                CosmosContainerMetadata.ETagPropertyName,
            };

            foreach (var path in _container.PartitionKeyPaths)
            {
                // Only a single-segment path maps onto a column name.
                var trimmed = path.TrimStart('/');
                if (trimmed.Length == 0 || trimmed.Contains('/'))
                    continue;

                if (names.Contains(trimmed) == false)
                    names.Add(trimmed);
            }

            return names;
        }

        /// <inheritdoc />
        public override RelDataType getRowType(RelDataTypeFactory typeFactory)
        {
            var varchar = typeFactory.createSqlType(SqlTypeName.VARCHAR);
            var any = typeFactory.createSqlType(SqlTypeName.ANY);

            var builder = typeFactory.builder();

            // The document itself. Every Cosmos query returns exactly one value per row, and this
            // is it; the promoted columns below are projections of the same document.
            builder.add(CosmosImplementor.MapColumnName, typeFactory.createMapType(varchar, any));

            foreach (var name in GetPromotedColumnNames())
            {
                var type = name switch
                {
                    CosmosContainerMetadata.TimestampPropertyName => typeFactory.createSqlType(SqlTypeName.BIGINT),
                    CosmosContainerMetadata.IdPropertyName => varchar,
                    CosmosContainerMetadata.ETagPropertyName => varchar,

                    // A declared path is guaranteed to exist but not to hold any particular type;
                    // documents in one container may disagree.
                    _ => any,
                };

                builder.add(name, type);
            }

            return builder.build();
        }

        /// <inheritdoc />
        public RelNode toRel(RelOptTable.ToRelContext context, RelOptTable relOptTable)
        {
            var cluster = context.getCluster();
            return new CosmosTableScan(cluster, cluster.traitSetOf(CosmosConvention.Create(_container)), relOptTable);
        }

    }

}
