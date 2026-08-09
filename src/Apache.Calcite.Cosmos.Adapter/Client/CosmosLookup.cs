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

            await foreach (var row in build.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                batch.Add(row);
                if (batch.Count < batchSize)
                    continue;

                await foreach (var joined in RunAsync(batch, executor, query, prefix, batchSize, buildKey, rowBuilder, probeKey, resultSelector, cancellationToken).ConfigureAwait(false))
                    yield return joined;

                batch.Clear();
            }

            if (batch.Count > 0)
                await foreach (var joined in RunAsync(batch, executor, query, prefix, batchSize, buildKey, rowBuilder, probeKey, resultSelector, cancellationToken).ConfigureAwait(false))
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
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            // Distinct, because the keys are data here rather than a predicate: a hundred build rows
            // over ten keys fetch ten. This is what the statement could not have done for itself.
            var keys = new List<object?>();
            var seen = new HashSet<object>();

            foreach (var row in batch)
                if (Normalize(buildKey(row)) is object key && seen.Add(key))
                    keys.Add(key);

            if (keys.Count == 0)
                yield break;

            var lookup = new Dictionary<object, List<TProbe>>();

            await foreach (var element in executor.ExecuteAsync(Bind(query, prefix, batchSize, keys), cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                var probe = rowBuilder(element);
                if (Normalize(probeKey(probe)) is not object key)
                    continue;

                if (lookup.TryGetValue(key, out var rows) == false)
                    lookup[key] = rows = new List<TProbe>();

                rows.Add(probe);
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
