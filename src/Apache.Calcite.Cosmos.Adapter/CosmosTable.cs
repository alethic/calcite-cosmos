using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using com.google.common.collect;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.type;
using org.apache.calcite.schema;
using org.apache.calcite.schema.impl;
using org.apache.calcite.sql.type;
using org.apache.calcite.util;

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
        readonly CosmosConvention _convention;
        readonly ICosmosQueryExecutor? _executor;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container this table exposes.</param>
        /// <param name="executor">Executes statements against the container, or <c>null</c> to expose a table that can be planned against but not read.</param>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public CosmosTable(CosmosContainerMetadata container, ICosmosQueryExecutor? executor = null)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _convention = CosmosConvention.Create(container);
            _executor = executor;
        }

        /// <summary>
        /// Gets the container this table exposes.
        /// </summary>
        public CosmosContainerMetadata Container => _container;

        /// <summary>
        /// Gets what executes statements against this container, or <c>null</c> when nothing can.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read by the converters out of the Cosmos convention, and read <em>when the plan runs</em> rather
        /// than when it is built: the expression they emit navigates to this table through the schema the
        /// query is executing against. A live client written into the compiled plan would tie that plan —
        /// which Calcite caches and re-executes — to whichever schema instance happened to be current when
        /// it was compiled.
        /// </para>
        /// <para>
        /// A table built from metadata alone has none. Planning is unaffected, since nothing about a
        /// statement or its cost depends on who runs it; enumerating the result of such a plan is what
        /// fails, and it fails saying so.
        /// </para>
        /// </remarks>
        public ICosmosQueryExecutor? Executor => _executor;

        /// <summary>
        /// Gets the calling convention for this container.
        /// </summary>
        /// <remarks>
        /// Held for the life of the table rather than created per scan. Conventions are traits and
        /// compare by identity, so a fresh instance per <see cref="toRel"/> would make two scans of
        /// the same container look like two different conventions and provoke converters between
        /// them.
        /// </remarks>
        public CosmosConvention Convention => _convention;

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
                // Only the service-generated properties are guaranteed present on every item.
                // A partition key path is declared, but a document may omit it — such items land
                // in the "none" logical partition. Declaring it non-nullable would licence the
                // planner to rewrite COUNT(x) into COUNT(*) and to reason about null placement in
                // ways the data does not support.
                var type = name switch
                {
                    CosmosContainerMetadata.TimestampPropertyName => typeFactory.createSqlType(SqlTypeName.BIGINT),
                    CosmosContainerMetadata.IdPropertyName => varchar,
                    CosmosContainerMetadata.ETagPropertyName => varchar,
                    _ => typeFactory.createTypeWithNullability(any, true),
                };

                builder.add(name, type);
            }

            return builder.build();
        }

        /// <summary>
        /// Returns the ordinal of a promoted column, or <c>-1</c> if the path is not promoted.
        /// </summary>
        /// <remarks>
        /// The map column occupies ordinal zero, so promoted columns begin at one.
        /// </remarks>
        /// <param name="policyPath">A path in policy form, such as <c>/category</c>.</param>
        /// <returns>The field ordinal, or <c>-1</c>.</returns>
        public int GetColumnOrdinal(string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return -1;

            var name = policyPath.TrimStart('/');
            if (name.Length == 0 || name.Contains('/'))
                return -1;

            var promoted = GetPromotedColumnNames();
            for (var i = 0; i < promoted.Count; i++)
                if (string.Equals(promoted[i], name, StringComparison.Ordinal))
                    return i + 1;

            return -1;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Derived entirely from declared facts. <c>id</c> is unique within a logical partition, so
        /// the partition key together with <c>id</c> is unique across the container — but only when
        /// every partition key path is promoted to a column, since a key is expressed over field
        /// ordinals. A nested partition key path yields no key at all rather than a wrong one.
        /// </para>
        /// <para>
        /// Row count comes from the service where the container was read from one, and is left unknown
        /// otherwise. It is a measurement rather than a sample: the rule against inferring from
        /// documents is about the <em>shape</em> of the data, where a wrong guess costs correctness. A
        /// wrong row count costs speed.
        /// </para>
        /// </remarks>
        public override Statistic getStatistic()
        {
            var keys = new java.util.ArrayList();
            var collations = new java.util.ArrayList();

            var partitionOrdinals = new java.util.ArrayList();
            var partitionPromoted = _container.PartitionKeyPaths.Count > 0;

            foreach (var path in _container.PartitionKeyPaths)
            {
                var ordinal = GetColumnOrdinal(path);
                if (ordinal < 0)
                {
                    partitionPromoted = false;
                    break;
                }

                partitionOrdinals.add(java.lang.Integer.valueOf(ordinal));
            }

            if (partitionPromoted)
            {
                var idOrdinal = GetColumnOrdinal("/" + CosmosContainerMetadata.IdPropertyName);
                if (idOrdinal >= 0)
                {
                    var bits = new java.util.ArrayList(partitionOrdinals);
                    bits.add(java.lang.Integer.valueOf(idOrdinal));
                    keys.add(ImmutableBitSet.builder().addAll(bits).build());
                }
            }

            // A composite index guarantees an ordering only when every one of its paths is a
            // column the planner can name.
            foreach (var index in _container.CompositeIndexes)
            {
                var fields = new java.util.ArrayList();
                var usable = true;

                foreach (var path in index.Paths)
                {
                    var ordinal = GetColumnOrdinal(path.Path);
                    if (ordinal < 0)
                    {
                        usable = false;
                        break;
                    }

                    fields.add(new RelFieldCollation(
                        ordinal,
                        path.Descending ? RelFieldCollation.Direction.DESCENDING : RelFieldCollation.Direction.ASCENDING));
                }

                if (usable)
                    collations.add(RelCollations.of(fields));
            }

            // A row count where the service gave one. It is approximate and lags, which is what a
            // planner row count is allowed to be; what it must not be is invented, and this is read
            // rather than sampled. Without it the planner compares plans with no sense of scale.
            var rowCount = _container.Statistics is CosmosContainerStatistics statistics
                ? java.lang.Double.valueOf(statistics.DocumentCount)
                : null;

            return Statistics.of(rowCount, keys, java.util.Collections.emptyList(), collations);
        }

        /// <inheritdoc />
        public RelNode toRel(RelOptTable.ToRelContext context, RelOptTable relOptTable)
        {
            var cluster = context.getCluster();
            return new CosmosTableScan(cluster, cluster.traitSetOf(_convention), relOptTable);
        }

    }

}
