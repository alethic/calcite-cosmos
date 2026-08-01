using org.apache.calcite.plan;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Scan of a Cosmos container, in the <see cref="CosmosConvention"/> calling convention.
    /// </summary>
    /// <remarks>
    /// Terminal: nothing composes beneath it. Cosmos has no derived tables and a query is scoped
    /// to a single container, so a scan is always the leaf of a pushed-down subtree.
    /// </remarks>
    public class CosmosTableScan : TableScan, CosmosRel
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="table">The table being scanned.</param>
        public CosmosTableScan(RelOptCluster cluster, RelTraitSet traitSet, RelOptTable table) :
            base(cluster, traitSet, java.util.Collections.emptyList(), table)
        {

        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            // The scan establishes the binding every operator above it resolves against.
            implementor.Fields = CosmosImplementor.BindFields(getRowType(), implementor.RootAlias);
        }

    }

}
