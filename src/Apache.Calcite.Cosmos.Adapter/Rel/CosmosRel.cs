using org.apache.calcite.rel;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Relational expression that uses the Cosmos calling convention.
    /// </summary>
    /// <remarks>
    /// Nodes implementing this interface are translated to Cosmos SQL rather than executed by
    /// Calcite's enumerable runtime.
    /// </remarks>
    public interface CosmosRel : RelNode
    {

    }

}
