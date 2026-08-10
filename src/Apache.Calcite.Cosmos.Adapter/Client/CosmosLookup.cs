using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Joins a sequence to a container by fetching only the documents its keys could match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relational join is not expressible in Cosmos, so without this both sides are read whole and
    /// joined in process. Here one side pays for the other: a batch of build rows contributes its
    /// distinct keys, and the statement run against the container carries them, so the service returns
    /// the documents that could join rather than all of them.
    /// </para>
    /// <para>
    /// This is Flink's lookup join rather than anything invented here. The correlation variable Calcite
    /// uses to express the shape is consumed by the rule and never reaches this point; what arrives is
    /// key values, which is what <c>asyncLookup(RowData)</c> receives there.
    /// </para>
    /// <para>
    /// The statement is rendered once, with a fixed number of key parameters. Only their values change
    /// per batch — which is why the batch size is fixed and a short batch pads rather than re-renders.
    /// </para>
    /// </remarks>
    public static class CosmosLookup
    {

        /// <summary>
        /// Reduces a key to a form two sides of a join can be compared in.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The two sides arrive by different routes — one from whatever the build side is, the other
        /// from JSON — so the same key can be an <see cref="int"/> on one side and a
        /// <see cref="long"/> or <see cref="double"/> on the other. Comparing the boxes directly would
        /// silently drop matching rows, which is the worst kind of wrong answer this could give.
        /// </para>
        /// <para>
        /// Every number is therefore compared as a <see cref="double"/>. That is what Cosmos stores —
        /// JSON has one numeric type — so it loses nothing the service was preserving. Types outside
        /// this set never arrive, because the rule declines a key that is not a string, a boolean, or a
        /// number.
        /// </para>
        /// </remarks>
        /// <param name="key">The key as either side produced it.</param>
        /// <returns>The comparable form, or <c>null</c> where the key is absent.</returns>
        public static object? Normalize(object? key)
        {
            return key switch
            {
                null => null,
                string or bool => key,
                sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal
                    => Convert.ToDouble(key, CultureInfo.InvariantCulture),
                _ => key,
            };
        }

        /// <summary>
        /// Builds the statement for one batch by binding its keys.
        /// </summary>
        /// <remarks>
        /// Padded to the number of parameters the statement was rendered with, by repeating a key it
        /// already carries. <c>k IN (a, b, b, b)</c> selects what <c>k IN (a, b)</c> selects, so the
        /// padding costs a longer statement and changes no answer — where re-rendering per batch would
        /// mean a statement that varies with the data.
        /// </remarks>
        /// <param name="query">The statement, rendered with <paramref name="parameterCount"/> key parameters.</param>
        /// <param name="prefix">The key parameters' name prefix.</param>
        /// <param name="parameterCount">How many key parameters the statement carries.</param>
        /// <param name="keys">The batch's distinct keys, at most <paramref name="parameterCount"/> of them.</param>
        /// <returns>The statement with every parameter bound.</returns>
        public static CosmosQuery Bind(CosmosQuery query, string prefix, int parameterCount, IReadOnlyList<object?> keys)
        {
            if (keys.Count == 0 || keys.Count > parameterCount)
                throw new ArgumentException($"A batch carries between 1 and {parameterCount} keys; {keys.Count} were supplied.", nameof(keys));

            var parameters = new List<CosmosParameter>(query.Parameters.Count + parameterCount);
            parameters.AddRange(query.Parameters);

            for (var i = 0; i < parameterCount; i++)
                parameters.Add(new CosmosParameter(prefix + i.ToString(CultureInfo.InvariantCulture), keys[i < keys.Count ? i : keys.Count - 1]));

            return query with { Parameters = parameters };
        }

        /// <summary>
        /// Joins <paramref name="build"/> to a container on one equality, fetching per batch.
        /// </summary>
        /// <remarks>
        /// <para>
        /// An inner join, and only an inner join. Anything else declines at the rule and is joined in
        /// process as before, which is what every other adapter does with every join.
        /// </para>
        /// <para>
        /// Rows are emitted in build order within a batch. The join is unordered, so this is not a
        /// promise — it is what falls out of probing in the order the batch was read, and keeping it
        /// costs nothing.
        /// </para>
        /// </remarks>
        /// <typeparam name="TBuild">The build side's row type.</typeparam>
        /// <typeparam name="TProbe">The container's row type.</typeparam>
        /// <typeparam name="TResult">The joined row type.</typeparam>
        /// <param name="build">The side whose keys are pushed down.</param>
        /// <param name="executor">Executes the statement.</param>
        /// <param name="query">The statement, rendered with <paramref name="batchSize"/> key parameters.</param>
        /// <param name="prefix">The key parameters' name prefix.</param>
        /// <param name="batchSize">How many build rows one fetch serves, and how many key parameters the statement carries.</param>
        /// <param name="buildKey">Reads the join key from a build row.</param>
        /// <param name="rowBuilder">Builds a container row from the JSON value it arrived as.</param>
        /// <param name="probeKey">Reads the join key from a container row.</param>
        /// <param name="resultSelector">Combines a matching pair.</param>
        /// <param name="cacheSize">
        /// How many keys to remember for the length of this join, or zero to remember none. Reference
        /// data is looked up repeatedly by definition, and a remembered key costs no request units at
        /// all.
        /// </param>
        /// <param name="shared">
        /// The container's cache across executions, or <c>null</c> where none is configured. Consulted
        /// beneath the per-join cache and populated by every fetch; see <c>DESIGN.md</c> under
        /// <em>The lookup join's caches</em>.
        /// </param>
        /// <param name="cancellationToken">Cancels the enumeration.</param>
        /// <returns>The joined rows.</returns>
        /// <exception cref="ArgumentNullException">Any required argument is <c>null</c>.</exception>
        public static async IAsyncEnumerable<TResult> JoinAsync<TBuild, TProbe, TResult>(
            IAsyncEnumerable<TBuild> build,
            ICosmosQueryExecutor executor,
            CosmosQuery query,
            string prefix,
            int batchSize,
            Func<TBuild, object?> buildKey,
            Func<JsonElement, TProbe> rowBuilder,
            Func<TProbe, object?> probeKey,
            Func<TBuild, TProbe, TResult> resultSelector,
            int cacheSize = 0,
            CosmosLookupCache? shared = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (build is null)
                throw new ArgumentNullException(nameof(build));
            if (executor is null)
                throw new ArgumentNullException(nameof(executor));
            if (buildKey is null)
                throw new ArgumentNullException(nameof(buildKey));
            if (rowBuilder is null)
                throw new ArgumentNullException(nameof(rowBuilder));
            if (probeKey is null)
                throw new ArgumentNullException(nameof(probeKey));
            if (resultSelector is null)
                throw new ArgumentNullException(nameof(resultSelector));
            if (batchSize < 1)
                throw new ArgumentOutOfRangeException(nameof(batchSize));

            var batch = new List<TBuild>(batchSize);

            // Held for the length of this join and no longer. A cache that outlived one execution
            // would have to answer for staleness, and this one cannot be stale in a way the join was
            // not already: the container is read across many requests either way, and remembering an
            // answer makes the result more self-consistent rather than less.
            var cache = cacheSize > 0 ? new Dictionary<object, List<TProbe>>() : null;

            // The identity the shared cache remembers this statement's answers under, computed once:
            // the batches differ only in their keys.
            var statement = shared is null ? null : CosmosLookupCache.Statement(query);

            await foreach (var row in build.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                batch.Add(row);
                if (batch.Count < batchSize)
                    continue;

                await foreach (var joined in RunAsync(batch, executor, query, prefix, batchSize, buildKey, rowBuilder, probeKey, resultSelector, cache, cacheSize, shared, statement, cancellationToken).ConfigureAwait(false))
                    yield return joined;

                batch.Clear();
            }

            if (batch.Count > 0)
                await foreach (var joined in RunAsync(batch, executor, query, prefix, batchSize, buildKey, rowBuilder, probeKey, resultSelector, cache, cacheSize, shared, statement, cancellationToken).ConfigureAwait(false))
                    yield return joined;
        }

        /// <summary>
        /// Fetches one batch and pairs it up.
        /// </summary>
        /// <remarks>
        /// A build row whose key is null matches nothing under an inner join — <c>null = x</c> is never
        /// true — so such rows contribute no key and take no part. A batch of only those fetches
        /// nothing at all, which is the whole point of doing this.
        /// </remarks>
        static async IAsyncEnumerable<TResult> RunAsync<TBuild, TProbe, TResult>(
            List<TBuild> batch,
            ICosmosQueryExecutor executor,
            CosmosQuery query,
            string prefix,
            int batchSize,
            Func<TBuild, object?> buildKey,
            Func<JsonElement, TProbe> rowBuilder,
            Func<TProbe, object?> probeKey,
            Func<TBuild, TProbe, TResult> resultSelector,
            Dictionary<object, List<TProbe>>? cache,
            int cacheSize,
            CosmosLookupCache? shared,
            string? statement,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Distinct, because the keys are data here rather than a predicate: a hundred build rows
            // over ten keys fetch ten. This is what the statement could not have done for itself.
            var keys = new List<object?>();
            var seen = new HashSet<object>();

            // What this batch resolves to, whether remembered or fetched.
            var lookup = new Dictionary<object, List<TProbe>>();

            foreach (var row in batch)
            {
                if (Normalize(buildKey(row)) is not object key || seen.Add(key) == false)
                    continue;

                if (cache is not null && cache.TryGetValue(key, out var remembered))
                {
                    lookup[key] = remembered;
                    continue;
                }

                // Beneath the per-join cache: an answer another execution fetched, rebuilt through
                // this plan's own row builder.
                if (shared is not null && shared.TryGet(statement!, key, out var held))
                {
                    var rows = new List<TProbe>(held.Count);
                    foreach (var element in held)
                        rows.Add(rowBuilder(element));

                    lookup[key] = rows;

                    if (cache is not null && cache.Count < cacheSize)
                        cache[key] = rows;

                    continue;
                }

                keys.Add(key);
            }

            if (keys.Count > 0)
            {
                var fetched = new Dictionary<object, List<TProbe>>();

                // What crossed the wire for each key, kept only where a shared cache will remember
                // it. The elements are already standalone: the executor clones what it yields.
                var elements = shared is null ? null : new Dictionary<object, List<JsonElement>>();

                await foreach (var element in executor.ExecuteAsync(Bind(query, prefix, batchSize, keys), cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    var probe = rowBuilder(element);
                    if (Normalize(probeKey(probe)) is not object key)
                        continue;

                    if (fetched.TryGetValue(key, out var rows) == false)
                        fetched[key] = rows = new List<TProbe>();

                    rows.Add(probe);

                    if (elements is not null)
                    {
                        if (elements.TryGetValue(key, out var held) == false)
                            elements[key] = held = new List<JsonElement>();

                        held.Add(element);
                    }
                }

                foreach (var key in keys)
                {
                    if (key is null)
                        continue;

                    // Absence is remembered too. A key the container has nothing for is the case a
                    // cache most needs to hold: without it, every batch mentioning that key asks
                    // again and is told nothing again.
                    var rows = fetched.TryGetValue(key, out var found) ? found : new List<TProbe>();

                    lookup[key] = rows;

                    // Filled to the bound and then left alone, rather than evicted. Nothing here knows
                    // which key is worth keeping, and a wrong eviction costs a request — so the simple
                    // rule is the honest one, and the bound is what stops a large build side from
                    // being remembered whole.
                    if (cache is not null && cache.Count < cacheSize)
                        cache[key] = rows;

                    if (shared is not null)
                        shared.Set(statement!, key, elements!.TryGetValue(key, out var held) ? held : Array.Empty<JsonElement>());
                }
            }

            foreach (var row in batch)
            {
                if (Normalize(buildKey(row)) is not object key || lookup.TryGetValue(key, out var matches) == false)
                    continue;

                foreach (var match in matches)
                    yield return resultSelector(row, match);
            }
        }

    }

}
