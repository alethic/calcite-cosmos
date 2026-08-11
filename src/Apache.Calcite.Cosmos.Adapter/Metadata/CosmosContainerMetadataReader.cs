using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Reads a container's declared metadata from the Cosmos service.
    /// </summary>
    /// <remarks>
    /// This is the only place container definitions are interpreted. Everything it produces is
    /// declared in the container definition — the partition key and the indexing policy — and
    /// nothing is inferred from the documents themselves.
    /// </remarks>
    public static class CosmosContainerMetadataReader
    {

        /// <summary>
        /// Reads the container's definition and projects it onto <see cref="CosmosContainerMetadata"/>.
        /// </summary>
        /// <param name="container">The container to read.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The container metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public static async Task<CosmosContainerMetadata> ReadAsync(Container container, CancellationToken cancellationToken = default)
        {
            if (container is null)
                throw new ArgumentNullException(nameof(container));

            var response = await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var metadata = FromProperties(response.Resource);

            // Attached rather than fetched. A schema exposes every container of a database, and at the
            // account level every container of every database, so reading the size of all of them to
            // plan against one is the wrong trade — and it is two more round trips each.
            //
            // The whole-partition delete capability is attached the same way and for a sharper
            // version of the same reason: it can only be learnt by attempting the operation, and a
            // plan with no whole-partition DELETE in it must never pay for that question.
            return metadata
                .WithStatisticsProvider(() => ReadStatisticsAsync(container, CancellationToken.None).GetAwaiter().GetResult())
                .WithPartitionKeyDeleteProbe(() => new Client.CosmosQueryExecutor(container).SupportsPartitionDeleteAsync(CancellationToken.None).GetAwaiter().GetResult());
        }

        /// <summary>
        /// Reads what the service says about a container's size and spread.
        /// </summary>
        /// <param name="container">The container to measure.</param>
        /// <param name="cancellationToken">Cancels the read.</param>
        /// <returns>The statistics, or <c>null</c> where the account did not answer.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public static async Task<CosmosContainerStatistics?> ReadStatisticsAsync(Container container, CancellationToken cancellationToken = default)
        {
            if (container is null)
                throw new ArgumentNullException(nameof(container));

            try
            {
                var response = await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                return await ReadStatisticsAsync(container, response, cancellationToken).ConfigureAwait(false);
            }
            catch (CosmosException)
            {
                return null;
            }
        }

        /// <summary>
        /// The header a container read carries its usage in.
        /// </summary>
        /// <remarks>
        /// Semicolon-separated <c>name=value</c> pairs — <c>documentsCount</c>, <c>documentsSize</c> in
        /// kilobytes, <c>collectionSize</c> — and the only route to a row count that does not cost a
        /// query over the container.
        /// </remarks>
        const string ResourceUsageHeader = "x-ms-resource-usage";

        /// <summary>
        /// Reads what the service says about the container's size and spread.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Returns <c>null</c> rather than throwing where any of it is unavailable. These are an
        /// optimisation: a planner with no row count compares plans as it did before, and a schema that
        /// fails to build because a header was missing is a worse outcome than a plan costed coarsely.
        /// </para>
        /// <para>
        /// The partition count comes from the feed ranges, one per physical partition. It is a second
        /// round trip, taken once when the schema is built.
        /// </para>
        /// </remarks>
        static async Task<CosmosContainerStatistics?> ReadStatisticsAsync(Container container, ContainerResponse response, CancellationToken cancellationToken)
        {
            try
            {
                var usage = ParseResourceUsage(response.Headers?.GetValueOrDefault(ResourceUsageHeader));

                if (usage.TryGetValue("documentsCount", out var count) == false)
                    return null;

                // Reported in kilobytes, and wanted in bytes: an average document size is the useful
                // form and kilobytes would round most documents to nothing.
                usage.TryGetValue("documentsSize", out var sizeInKilobytes);

                var ranges = await container.GetFeedRangesAsync(cancellationToken).ConfigureAwait(false);

                return new CosmosContainerStatistics(count, sizeInKilobytes * 1024L, ranges?.Count ?? 1);
            }
            catch (CosmosException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parses the semicolon-separated <c>name=value</c> pairs of the resource usage header.
        /// </summary>
        static Dictionary<string, long> ParseResourceUsage(string? header)
        {
            var values = new Dictionary<string, long>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(header))
                return values;

            foreach (var pair in header.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = pair.IndexOf('=');
                if (separator <= 0)
                    continue;

                if (long.TryParse(pair.AsSpan(separator + 1), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value))
                    values[pair.Substring(0, separator).Trim()] = value;
            }

            return values;
        }

        /// <summary>
        /// Projects a container definition onto <see cref="CosmosContainerMetadata"/>.
        /// </summary>
        /// <param name="properties">The container definition.</param>
        /// <returns>The container metadata.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="properties"/> is <c>null</c>.</exception>
        public static CosmosContainerMetadata FromProperties(ContainerProperties properties)
        {
            if (properties is null)
                throw new ArgumentNullException(nameof(properties));

            return new CosmosContainerMetadata(
                properties.Id,
                ReadPartitionKeyPaths(properties),
                ReadCompositeIndexes(properties.IndexingPolicy),
                ReadPaths(properties.IndexingPolicy?.IncludedPaths, x => x.Path),
                ReadPaths(properties.IndexingPolicy?.ExcludedPaths, x => x.Path));
        }

        /// <summary>
        /// Reads the partition key paths, outermost first.
        /// </summary>
        /// <remarks>
        /// <c>PartitionKeyPaths</c> covers both the single and hierarchical cases; the singular
        /// <c>PartitionKeyPath</c> property is not usable for a hierarchical key.
        /// </remarks>
        static IReadOnlyList<string> ReadPartitionKeyPaths(ContainerProperties properties)
        {
            var paths = new List<string>();

            var declared = properties.PartitionKeyPaths;
            if (declared is not null)
                foreach (var path in declared)
                    if (string.IsNullOrEmpty(path) == false)
                        paths.Add(NormalizePath(path));

            return paths;
        }

        /// <summary>
        /// Reads a collection of indexing policy path patterns.
        /// </summary>
        /// <remarks>
        /// Trailing <c>/?</c> and <c>/*</c> specifiers are preserved here, unlike composite index
        /// paths: they carry the precedence that decides which of a conflicting pair wins.
        /// </remarks>
        static IReadOnlyList<string> ReadPaths<T>(IEnumerable<T>? paths, Func<T, string> selector)
        {
            var values = new List<string>();
            if (paths is null)
                return values;

            foreach (var path in paths)
            {
                var value = selector(path);
                if (string.IsNullOrEmpty(value) == false)
                    values.Add(value);
            }

            return values;
        }

        /// <summary>
        /// Reads the composite indexes declared by an indexing policy.
        /// </summary>
        /// <remarks>
        /// Definitions with fewer than two paths are skipped rather than rejected: they cannot
        /// serve a multi-key sort, which is the only thing composite indexes are consulted for
        /// here, and refusing to build metadata over an otherwise-valid container would be worse
        /// than ignoring one.
        /// </remarks>
        static IReadOnlyList<CosmosCompositeIndex> ReadCompositeIndexes(IndexingPolicy? policy)
        {
            var indexes = new List<CosmosCompositeIndex>();
            if (policy?.CompositeIndexes is null)
                return indexes;

            foreach (var definition in policy.CompositeIndexes)
            {
                if (definition is null || definition.Count < 2)
                    continue;

                var paths = new List<CosmosCompositeIndexPath>(definition.Count);
                foreach (var path in definition)
                    paths.Add(new CosmosCompositeIndexPath(
                        NormalizePath(path.Path),
                        path.Order == CompositePathSortOrder.Descending));

                indexes.Add(new CosmosCompositeIndex(paths));
            }

            return indexes;
        }

        /// <summary>
        /// Reduces an indexing policy path to the plain form paths are compared in.
        /// </summary>
        /// <remarks>
        /// Policy paths may carry a trailing <c>/?</c> or <c>/*</c> specifier. Composite index
        /// paths conventionally omit it, but strip it defensively so that comparison against a
        /// path produced by <see cref="Sql.CosmosPath.ToPolicyPath"/> does not depend on which
        /// form the service returned.
        /// </remarks>
        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return path;

            if (path.EndsWith("/?", StringComparison.Ordinal) || path.EndsWith("/*", StringComparison.Ordinal))
                path = path.Substring(0, path.Length - 2);

            return path;
        }

    }

}
