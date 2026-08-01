using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.rel;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Relational expression that uses the Cosmos calling convention.
    /// </summary>
    /// <remarks>
    /// Nodes implementing this interface are translated to Cosmos SQL rather than executed by
    /// Calcite's enumerable runtime. Rather than returning a fragment, each node contributes its
    /// clause to the implementor, which renders the whole statement once the subtree has been
    /// visited — Cosmos has no derived tables, so there is nothing to compose fragments into.
    /// </remarks>
    public interface CosmosRel : RelNode
    {

        /// <summary>
        /// Contributes this node's contribution to the statement under construction.
        /// </summary>
        /// <param name="implementor">The statement being built.</param>
        void Implement(CosmosImplementor implementor);

    }

}
