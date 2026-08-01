using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// The declared and service-guaranteed facts about a container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A container has no row schema — two items may share nothing but <c>id</c>. What it does
    /// have is planner metadata: a partition key, an indexing policy, unique key constraints. This
    /// type carries that, and nothing inferred from sampling documents. An inferred key or
    /// collation would produce a silently incorrect plan rather than a slow one.
    /// </para>
    /// <para>
    /// Everything here originates from the container definition or is guaranteed by the service.
    /// </para>
    /// </remarks>
    public sealed class CosmosContainerMetadata
    {

        /// <summary>
        /// The property every item carries, unique within a logical partition.
        /// </summary>
        public const string IdPropertyName = "id";

        /// <summary>
        /// The service-maintained last-modified timestamp, in epoch seconds.
        /// </summary>
        /// <remarks>
        /// The only temporal value in a container whose encoding is defined rather than a matter
        /// of application convention.
        /// </remarks>
        public const string TimestampPropertyName = "_ts";

        /// <summary>
        /// The service-maintained entity tag used for optimistic concurrency.
        /// </summary>
        public const string ETagPropertyName = "_etag";

        readonly string _name;
        readonly string[] _partitionKeyPaths;
        readonly CosmosCompositeIndex[] _compositeIndexes;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The container name.</param>
        /// <param name="partitionKeyPaths">The partition key paths in policy form, outermost first. Cosmos permits up to three for a hierarchical key.</param>
        /// <param name="compositeIndexes">The composite indexes declared by the indexing policy.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is <c>null</c> or empty.</exception>
        public CosmosContainerMetadata(string name, IEnumerable<string>? partitionKeyPaths = null, IEnumerable<CosmosCompositeIndex>? compositeIndexes = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"'{nameof(name)}' cannot be null or empty.", nameof(name));

            _name = name;
            _partitionKeyPaths = partitionKeyPaths is null ? Array.Empty<string>() : new List<string>(partitionKeyPaths).ToArray();
            _compositeIndexes = compositeIndexes is null ? Array.Empty<CosmosCompositeIndex>() : new List<CosmosCompositeIndex>(compositeIndexes).ToArray();

            if (_partitionKeyPaths.Length > 3)
                throw new ArgumentException("A container may declare at most three partition key paths.", nameof(partitionKeyPaths));
        }

        /// <summary>
        /// Gets the container name.
        /// </summary>
        public string Name => _name;

        /// <summary>
        /// Gets the partition key paths in policy form, outermost first.
        /// </summary>
        public IReadOnlyList<string> PartitionKeyPaths => _partitionKeyPaths;

        /// <summary>
        /// Gets the composite indexes declared by the indexing policy.
        /// </summary>
        public IReadOnlyList<CosmosCompositeIndex> CompositeIndexes => _compositeIndexes;

        /// <summary>
        /// Determines whether an <c>ORDER BY</c> over the given keys is legal against this container.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is a legality test, not a cost estimate. The distinction matters:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// A sort on a single property is always legal. If that property happens not to be
        /// indexed the query is more expensive, but it still runs.
        /// </description></item>
        /// <item><description>
        /// A sort on two or more properties requires a matching composite index. Without one the
        /// service rejects the query outright, so pushing it down would be a defect rather than a
        /// pessimisation.
        /// </description></item>
        /// </list>
        /// <para>
        /// Conversion rules must consult this before converting a <c>Sort</c>.
        /// </para>
        /// </remarks>
        /// <param name="keys">The requested sort keys, in order.</param>
        /// <returns><c>true</c> if the sort may be pushed down; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <c>null</c>.</exception>
        public bool IsSortSupported(IReadOnlyList<CosmosSortKey> keys)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            if (keys.Count <= 1)
                return true;

            foreach (var index in _compositeIndexes)
                if (index.Supports(keys))
                    return true;

            return false;
        }

    }

}
