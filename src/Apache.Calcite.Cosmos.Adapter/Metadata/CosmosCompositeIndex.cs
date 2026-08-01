using System;
using System.Collections.Generic;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// One path within a composite index, together with the direction it is ordered in.
    /// </summary>
    /// <param name="Path">The policy-form path, such as <c>/inventory/quantity</c>.</param>
    /// <param name="Descending">Whether the path is indexed descending.</param>
    public readonly record struct CosmosCompositeIndexPath(string Path, bool Descending);

    /// <summary>
    /// A sort key requested of a container.
    /// </summary>
    /// <param name="Path">The policy-form path being sorted on.</param>
    /// <param name="Descending">Whether the sort is descending.</param>
    public readonly record struct CosmosSortKey(string Path, bool Descending);

    /// <summary>
    /// An ordered composite index declared on a container.
    /// </summary>
    /// <remarks>
    /// Composite indexes are what make a multi-property <c>ORDER BY</c> legal. Without a matching
    /// one, such a query is rejected by the service rather than merely running slowly.
    /// </remarks>
    public sealed class CosmosCompositeIndex
    {

        readonly CosmosCompositeIndexPath[] _paths;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="paths">The index paths, in the order they are indexed.</param>
        /// <exception cref="ArgumentNullException"><paramref name="paths"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException"><paramref name="paths"/> has fewer than two entries.</exception>
        public CosmosCompositeIndex(IEnumerable<CosmosCompositeIndexPath> paths)
        {
            if (paths is null)
                throw new ArgumentNullException(nameof(paths));

            _paths = new List<CosmosCompositeIndexPath>(paths).ToArray();
            if (_paths.Length < 2)
                throw new ArgumentException("A composite index requires at least two paths.", nameof(paths));
        }

        /// <summary>
        /// Gets the index paths, in the order they are indexed.
        /// </summary>
        public IReadOnlyList<CosmosCompositeIndexPath> Paths => _paths;

        /// <summary>
        /// Determines whether this index can serve the given <c>ORDER BY</c> keys.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Three conditions must hold, per the documented composite index rules:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// The paths must match the sort sequence positionally.
        /// </description></item>
        /// <item><description>
        /// The lengths must be equal. A composite index on
        /// <c>(name, quantity, timestamp)</c> does <em>not</em> serve
        /// <c>ORDER BY name, quantity</c> — prefixes do not qualify.
        /// </description></item>
        /// <item><description>
        /// The directions must match on every path, or be inverted on every path. A partial
        /// direction match does not qualify.
        /// </description></item>
        /// </list>
        /// </remarks>
        /// <param name="keys">The requested sort keys, in order.</param>
        /// <returns><c>true</c> if this index serves the sort; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="keys"/> is <c>null</c>.</exception>
        public bool Supports(IReadOnlyList<CosmosSortKey> keys)
        {
            if (keys is null)
                throw new ArgumentNullException(nameof(keys));

            if (keys.Count != _paths.Length)
                return false;

            for (var i = 0; i < keys.Count; i++)
                if (string.Equals(_paths[i].Path, keys[i].Path, StringComparison.Ordinal) == false)
                    return false;

            var sameThroughout = true;
            var invertedThroughout = true;

            for (var i = 0; i < keys.Count; i++)
            {
                if (_paths[i].Descending == keys[i].Descending)
                    invertedThroughout = false;
                else
                    sameThroughout = false;
            }

            return sameThroughout || invertedThroughout;
        }

    }

}
