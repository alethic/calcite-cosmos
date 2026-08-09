using System;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;

using org.apache.calcite.plan;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// Calcite calling convention that identifies relational nodes whose results are produced by
    /// executing Cosmos SQL against an Azure Cosmos DB container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Like the JDBC convention, <see cref="CosmosConvention"/> is not a singleton: each instance
    /// is bound to a particular container. When a query touches two different containers the
    /// planner uses two separate convention instances and inserts the necessary converter nodes
    /// between them.
    /// </para>
    /// <para>
    /// The convention carries the container's metadata because conversion rules need it. Whether a
    /// <c>Sort</c> may be pushed down at all depends on the container's composite indexes, which
    /// is unusual for a planner rule but unavoidable here — a multi-key sort without a matching
    /// index is rejected by the service rather than merely being slow.
    /// </para>
    /// </remarks>
    public class CosmosConvention : Convention.Impl
    {

        /// <summary>
        /// Relative cost multiplier applied to Cosmos relational nodes during planning. A value
        /// below 1.0 makes the planner prefer pushing operations into Cosmos over equivalent
        /// in-memory implementations.
        /// </summary>
        public const double CostMultiplier = .8d;

        /// <summary>
        /// Creates a new <see cref="CosmosConvention"/> for the given container.
        /// </summary>
        /// <param name="container">The container this convention is bound to.</param>
        /// <returns>A new <see cref="CosmosConvention"/> instance.</returns>
        public static CosmosConvention Create(CosmosContainerMetadata container)
        {
            return new CosmosConvention(container);
        }

        readonly CosmosContainerMetadata _container;

        /// <summary>
        /// Initializes a new instance of <see cref="CosmosConvention"/>. Prefer
        /// <see cref="Create"/> over calling this constructor directly.
        /// </summary>
        /// <param name="container">The container this convention is bound to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public CosmosConvention(CosmosContainerMetadata container) :
            base("COSMOS." + (container ?? throw new ArgumentNullException(nameof(container))).Name, typeof(CosmosRel))
        {
            _container = container;
        }

        /// <summary>
        /// Gets the container this convention is bound to.
        /// </summary>
        /// <remarks>
        /// Metadata only. What executes a statement belongs to the table rather than to the convention,
        /// and a plan reaches it through the schema when it runs — see <see cref="CosmosTable.Executor"/>.
        /// A convention is a planning-time trait, and a client is not a planning-time fact.
        /// </remarks>
        public CosmosContainerMetadata Container => _container;

        /// <inheritdoc />
        public override void register(RelOptPlanner planner)
        {
            foreach (var rule in CosmosRules.GetRules(this))
                planner.addRule(rule);
        }

    }

}
