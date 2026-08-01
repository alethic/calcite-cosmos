using System;

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
    /// Like the JDBC convention, <see cref="CosmosConvention"/> is not a singleton: each instance is
    /// bound to a particular container. When a query touches two different containers the planner
    /// uses two separate convention instances and inserts the necessary converter nodes between them.
    /// </para>
    /// <para>
    /// Use <see cref="Create"/> to obtain an instance.
    /// </para>
    /// </remarks>
    public class CosmosConvention : Convention.Impl
    {

        /// <summary>
        /// Relative cost multiplier applied to Cosmos relational nodes during planning. A value below
        /// 1.0 makes the planner prefer pushing operations into Cosmos over equivalent in-memory
        /// implementations.
        /// </summary>
        public const double CostMultiplier = .8d;

        /// <summary>
        /// Creates a new <see cref="CosmosConvention"/> with the given display name.
        /// </summary>
        /// <param name="name">A short display name appended to the convention identifier (e.g. the schema name).</param>
        /// <returns>A new <see cref="CosmosConvention"/> instance.</returns>
        public static CosmosConvention Create(string name)
        {
            return new CosmosConvention(name);
        }

        /// <summary>
        /// Initializes a new instance of <see cref="CosmosConvention"/>. Prefer <see cref="Create"/>
        /// over calling this constructor directly.
        /// </summary>
        /// <param name="name">A short display name appended to the convention identifier.</param>
        public CosmosConvention(string name) :
            base("COSMOS." + name, typeof(CosmosRel))
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"'{nameof(name)}' cannot be null or empty.", nameof(name));
        }

    }

}
