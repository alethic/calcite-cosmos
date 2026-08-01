using org.apache.calcite.rel.convert;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Base class for planner rules that convert a standard relational operator to its
    /// Cosmos-convention counterpart.
    /// </summary>
    /// <remarks>
    /// Every rule deriving from this must decline — by returning <c>null</c> from
    /// <c>convert</c> — whenever the operator cannot be expressed faithfully in Cosmos SQL.
    /// Declining leaves the operator to Calcite's in-process runtime, which is always correct.
    /// Converting optimistically and failing later during implementation is not.
    /// </remarks>
    public abstract class CosmosConverterRule : ConverterRule
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="config">The rule configuration.</param>
        protected CosmosConverterRule(Config config) :
            base(config)
        {

        }

    }

}
