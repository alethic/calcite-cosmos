using org.apache.calcite.rel;
using org.apache.calcite.schema;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// What a container tells the planner about itself.
    /// </summary>
    /// <remarks>
    /// Implemented rather than built with <see cref="Statistics"/>, whose factory methods carry a
    /// row count, keys, referential constraints and collations but not a distribution — which the
    /// interface declares and a Cosmos container can state. See <see cref="CosmosTable.getStatistic"/>
    /// for what is reported and what is deliberately withheld.
    /// </remarks>
    class CosmosStatistic : Statistic
    {

        readonly java.lang.Double? _rowCount;
        readonly java.util.List _keys;
        readonly RelDistribution _distribution;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rowCount">The row count the service reported, or <c>null</c> where it was not asked.</param>
        /// <param name="keys">The unique keys, expressed over field ordinals.</param>
        /// <param name="distribution">How the container's rows are spread.</param>
        public CosmosStatistic(java.lang.Double? rowCount, java.util.List keys, RelDistribution distribution)
        {
            _rowCount = rowCount;
            _keys = keys;
            _distribution = distribution;
        }

        /// <inheritdoc />
        public java.lang.Double? getRowCount() => _rowCount;

        /// <inheritdoc />
        public java.util.List getKeys() => _keys;

        /// <inheritdoc />
        public bool isKey(ImmutableBitSet columns)
        {
            for (var i = 0; i < _keys.size(); i++)
                if (columns.contains((ImmutableBitSet)_keys.get(i)))
                    return true;

            return false;
        }

        /// <inheritdoc />
        public java.util.List getReferentialConstraints() => java.util.Collections.emptyList();

        /// <inheritdoc />
        /// <remarks>
        /// Empty, and deliberately: a statistic's collations are the order a scan's rows already
        /// arrive in, and Cosmos guarantees none without an <c>ORDER BY</c> whatever is indexed.
        /// See <see cref="CosmosTable.getStatistic"/>.
        /// </remarks>
        public java.util.List getCollations() => java.util.Collections.emptyList();

        /// <inheritdoc />
        public RelDistribution getDistribution() => _distribution;

    }

}
