using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using org.apache.calcite.plan;
using org.apache.calcite.schema;
using org.apache.calcite.sql2rel;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// Says which of a container's columns an <c>INSERT</c> may omit, and which it may not name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this an insert does not reach a conversion rule at all. Every column that is neither
    /// nullable nor defaulted must be supplied, which <c>SqlValidatorImpl.checkFieldCount</c> enforces,
    /// and both <c>_MAP</c> and <c>id</c> are declared <c>NOT NULL</c>:
    /// </para>
    /// <code>
    /// Column '_MAP' has no default value and does not allow NULLs
    /// </code>
    /// <para>
    /// Both declarations are true — every document has an <c>id</c>, and the map column is the
    /// document — so the answer is not to weaken the row type. A row type that bends to what a caller
    /// wants to omit is a suggestion rather than a declaration, and it would weaken the planner
    /// metadata expressed over it for every read as well.
    /// </para>
    /// <para>
    /// <see cref="ColumnStrategy"/> is Calcite's own separation of the two things being confused here:
    /// <em>not null in the table</em> and <em>optional in an insert</em>. A <c>DEFAULT</c> column may be
    /// omitted and defaults to null, which the write path reads as "not supplied"; a <c>VIRTUAL</c>
    /// column may be read but not written, so the validator refuses to let one be named.
    /// </para>
    /// </remarks>
    class CosmosColumnStrategies : NullInitializerExpressionFactory
    {

        readonly CosmosTable _table;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="table">The table whose columns are described.</param>
        /// <exception cref="ArgumentNullException"><paramref name="table"/> is <c>null</c>.</exception>
        public CosmosColumnStrategies(CosmosTable table)
        {
            _table = table ?? throw new ArgumentNullException(nameof(table));
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// <c>_ts</c> and <c>_etag</c> are <c>STORED</c>: the service assigns both on every write, so a
        /// value supplied for either would be ignored or refused. Saying so here makes naming one a
        /// validation error rather than something the adapter drops later without comment.
        /// </para>
        /// <para>
        /// <b><c>STORED</c> rather than <c>VIRTUAL</c>, and the difference is not cosmetic.</b> Both
        /// refuse a write, but <c>VIRTUAL</c> also means <em>not stored</em>: measured, it makes
        /// <c>RelOptTableImpl.toRel</c> drop the column from the scan and project a literal null in its
        /// place, so <c>SELECT _ts</c> returns nothing the service holds. These two columns are stored —
        /// they are the service's own, and among the more useful things a caller reads.
        /// </para>
        /// <para>
        /// Everything else is <c>DEFAULT</c>. The map column and the promoted columns describe one
        /// document between them, and which of them a caller uses is the caller's choice — see
        /// <em>What an insert writes</em> in <c>DESIGN.md</c>, where the rule for combining them is
        /// recorded.
        /// </para>
        /// </remarks>
        public override ColumnStrategy generationStrategy(RelOptTable table, int iColumn)
        {
            // The map column, which is the document itself.
            if (iColumn == CosmosImplementor.MapColumnOrdinal)
                return ColumnStrategy.DEFAULT;

            var promoted = _table.GetPromotedColumnNames();
            var index = iColumn - 1;

            if (index < 0 || index >= promoted.Count)
                return base.generationStrategy(table, iColumn);

            return promoted[index] switch
            {
                CosmosContainerMetadata.TimestampPropertyName => ColumnStrategy.STORED,
                CosmosContainerMetadata.ETagPropertyName => ColumnStrategy.STORED,
                _ => ColumnStrategy.DEFAULT,
            };
        }

    }

}
