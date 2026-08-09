using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// The sequence a compiled plan reads its rows from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what the converter puts on the per-row path, and the only part of the adapter that runs
    /// once per row. Everything else — rendering the statement, choosing the partition key, building the
    /// row builder — happens once, while the statement is being prepared.
    /// </para>
    /// <para>
    /// There is no synchronous counterpart, and that is a fact about Cosmos rather than a gap here. The
    /// v3 SDK has no synchronous data-plane API at all: a page arrives only through
    /// <c>FeedIterator.ReadNextAsync</c>. An <see cref="IEnumerable{T}"/> over it could only wait on each
    /// page, blocking a thread for the length of a network round trip — the sync-over-async pull that
    /// <c>ClrAsyncEnumerableConvention</c> exists to keep out of a plan.
    /// </para>
    /// </remarks>
    public static class CosmosSequences
    {

        /// <summary>
        /// Executes a statement and reads its results as rows, without blocking.
        /// </summary>
        /// <typeparam name="TRow">The plan's row type.</typeparam>
        /// <param name="executor">Executes the statement.</param>
        /// <param name="query">The statement and its bound parameters.</param>
        /// <param name="rowBuilder">Builds one row from the JSON value Cosmos returned for it.</param>
        /// <param name="cancellationToken">Cancels the enumeration.</param>
        /// <returns>The rows.</returns>
        /// <remarks>
        /// The shape the Cosmos SDK already has. A page is awaited rather than waited for, so a query that
        /// spans many continuations occupies no thread between them.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="executor"/> or <paramref name="rowBuilder"/> is <c>null</c>.</exception>
        public static async IAsyncEnumerable<TRow> ReadAsync<TRow>(ICosmosQueryExecutor executor, CosmosQuery query, Func<JsonElement, TRow> rowBuilder, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (executor is null)
                throw new ArgumentNullException(nameof(executor));
            if (rowBuilder is null)
                throw new ArgumentNullException(nameof(rowBuilder));

            await foreach (var element in executor.ExecuteAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false))
                yield return rowBuilder(element);
        }

    }

}
