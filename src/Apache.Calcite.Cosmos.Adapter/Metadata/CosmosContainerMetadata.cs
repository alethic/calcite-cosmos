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
        readonly string[] _includedPaths;
        readonly string[] _excludedPaths;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="name">The container name.</param>
        /// <param name="partitionKeyPaths">The partition key paths in policy form, outermost first. Cosmos permits up to three for a hierarchical key.</param>
        /// <param name="compositeIndexes">The composite indexes declared by the indexing policy.</param>
        /// <param name="includedPaths">The indexing policy's included path patterns.</param>
        /// <param name="excludedPaths">The indexing policy's excluded path patterns.</param>
        /// <param name="statistics">What the service reports about the container's size, or <c>null</c> where it was not asked.</param>
        /// <exception cref="ArgumentException"><paramref name="name"/> is <c>null</c> or empty.</exception>
        public CosmosContainerMetadata(
            string name,
            IEnumerable<string>? partitionKeyPaths = null,
            IEnumerable<CosmosCompositeIndex>? compositeIndexes = null,
            IEnumerable<string>? includedPaths = null,
            IEnumerable<string>? excludedPaths = null,
            CosmosContainerStatistics? statistics = null)
        {
            if (string.IsNullOrEmpty(name))
                throw new ArgumentException($"'{nameof(name)}' cannot be null or empty.", nameof(name));

            _name = name;
            _partitionKeyPaths = partitionKeyPaths is null ? Array.Empty<string>() : new List<string>(partitionKeyPaths).ToArray();
            _compositeIndexes = compositeIndexes is null ? Array.Empty<CosmosCompositeIndex>() : new List<CosmosCompositeIndex>(compositeIndexes).ToArray();
            _includedPaths = includedPaths is null ? Array.Empty<string>() : new List<string>(includedPaths).ToArray();
            Statistics = statistics;
            _excludedPaths = excludedPaths is null ? Array.Empty<string>() : new List<string>(excludedPaths).ToArray();

            if (_partitionKeyPaths.Length > 3)
                throw new ArgumentException("A container may declare at most three partition key paths.", nameof(partitionKeyPaths));
        }

        /// <summary>
        /// Gets the indexing policy's included path patterns.
        /// </summary>
        public IReadOnlyList<string> IncludedPaths => _includedPaths;

        /// <summary>
        /// Gets the indexing policy's excluded path patterns.
        /// </summary>
        public IReadOnlyList<string> ExcludedPaths => _excludedPaths;

        /// <summary>
        /// Determines whether a path is covered by the container's index.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This bears on cost, never on legality: a predicate or sort over an unindexed path still
        /// runs, it just scans. The default policy indexes everything, so a container that declares
        /// no included or excluded paths reports every path as indexed.
        /// </para>
        /// <para>
        /// Where included and excluded patterns conflict the more precise wins — a deeper path
        /// beats a shallower one, and <c>/?</c> beats <c>/*</c> at the same depth. <c>id</c> and
        /// <c>_ts</c> are always indexed and cannot be excluded.
        /// </para>
        /// </remarks>
        /// <param name="policyPath">The path in policy form, such as <c>/inventory/quantity</c>.</param>
        /// <returns><c>true</c> if the path is indexed; otherwise <c>false</c>.</returns>
        public bool IsPathIndexed(string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return false;

            if (string.Equals(policyPath, "/" + IdPropertyName, StringComparison.Ordinal) ||
                string.Equals(policyPath, "/" + TimestampPropertyName, StringComparison.Ordinal))
                return true;

            // No declared policy means the default, which indexes every property.
            if (_includedPaths.Length == 0 && _excludedPaths.Length == 0)
                return true;

            var included = BestMatch(_includedPaths, policyPath);
            var excluded = BestMatch(_excludedPaths, policyPath);

            // A tie favours inclusion, as does the absence of any exclusion.
            return included >= excluded && included >= 0;
        }

        /// <summary>
        /// Returns the specificity of the closest matching pattern, or <c>-1</c> when none match.
        /// </summary>
        static int BestMatch(IReadOnlyList<string> patterns, string path)
        {
            var best = -1;

            foreach (var pattern in patterns)
            {
                var score = Specificity(pattern, path);
                if (score > best)
                    best = score;
            }

            return best;
        }

        /// <summary>
        /// Scores how precisely a policy pattern matches a path.
        /// </summary>
        /// <remarks>
        /// A <c>/?</c> pattern addresses one scalar and must match exactly; a <c>/*</c> pattern
        /// addresses a subtree and matches any path beneath it. Depth is the primary ranking, with
        /// <c>/?</c> outranking <c>/*</c> at equal depth.
        /// </remarks>
        static int Specificity(string pattern, string path)
        {
            if (string.IsNullOrEmpty(pattern))
                return -1;

            var exact = pattern.EndsWith("/?", StringComparison.Ordinal);
            var subtree = pattern.EndsWith("/*", StringComparison.Ordinal);

            var prefix = exact || subtree ? pattern.Substring(0, pattern.Length - 2) : pattern;

            // The root pattern "/*" reduces to an empty prefix and matches everything.
            if (prefix.Length == 0)
                return subtree || exact ? 0 : -1;

            if (exact)
            {
                if (string.Equals(prefix, path, StringComparison.Ordinal) == false)
                    return -1;
            }
            else if (string.Equals(prefix, path, StringComparison.Ordinal) == false &&
                     path.StartsWith(prefix + "/", StringComparison.Ordinal) == false)
            {
                return -1;
            }

            var depth = 0;
            foreach (var c in prefix)
                if (c == '/')
                    depth++;

            // Two ranks per level leaves room for /? to outrank /* at the same depth.
            return (depth * 2) + (exact ? 1 : 0);
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
        /// Gets what the service reports about the container's size, or <c>null</c> where nothing asked.
        /// </summary>
        /// <remarks>
        /// A measurement rather than a declaration, and optional for that reason: a container built from
        /// a definition alone — which is what every test that does not need a service uses — has none,
        /// and the planner falls back to comparing plans without a row count exactly as it did before.
        /// </remarks>
        public CosmosContainerStatistics? Statistics { get; }

        /// <summary>
        /// Returns the same metadata carrying the given statistics.
        /// </summary>
        /// <remarks>
        /// The declaration and the measurement are read from different places — the definition and a
        /// response header — so they are attached in two steps rather than threaded through every
        /// constructor call.
        /// </remarks>
        /// <param name="statistics">What the service reports, or <c>null</c>.</param>
        /// <returns>The metadata.</returns>
        public CosmosContainerMetadata WithStatistics(CosmosContainerStatistics? statistics)
        {
            return statistics is null
                ? this
                : new CosmosContainerMetadata(_name, _partitionKeyPaths, _compositeIndexes, _includedPaths, _excludedPaths, statistics);
        }

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
