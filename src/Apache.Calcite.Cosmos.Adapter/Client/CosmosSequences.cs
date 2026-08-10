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

        /// <summary>
        /// Applies a write to every row of a sequence, and yields how many rows it affected.
        /// </summary>
        /// <typeparam name="TRow">The input plan's row type.</typeparam>
        /// <typeparam name="TResult">The modify's own row type, which is one row count.</typeparam>
        /// <param name="source">The rows describing what to write.</param>
        /// <param name="writer">Writes documents to the container.</param>
        /// <param name="write">What to do with each row, and how to read one.</param>
        /// <param name="fields">Reads a row's values out, boxed and in field order.</param>
        /// <param name="result">Wraps the count as the modify's own row.</param>
        /// <param name="cancellationToken">Cancels the enumeration.</param>
        /// <returns>Exactly one row, carrying the number of documents affected.</returns>
        /// <remarks>
        /// <para>
        /// <b>Both operations build the document the row describes, and that is not a detail.</b> A
        /// delete needs an <c>id</c> and a partition key, and the partition key may be at a nested path
        /// that is not promoted to a column — so reading it out of the assembled document is the one
        /// route that works for every container, rather than one that works until a container declares
        /// <c>/inventory/sku</c> as its key.
        /// </para>
        /// <para>
        /// One request per row. The count is yielded at the end, so the sequence is only complete once
        /// every write is, and abandoning the enumeration stops it — which is the same cancellation the
        /// read path has, for the same reason.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
        /// <exception cref="CosmosExecutionException">A row does not describe a document the operation can be applied to.</exception>
        public static async IAsyncEnumerable<TResult> WriteAsync<TRow, TResult>(
            IAsyncEnumerable<TRow> source,
            ICosmosItemWriter writer,
            CosmosWrite write,
            Func<TRow, object?[]> fields,
            Func<long, TResult> result,
            CosmosLookupCache? invalidate = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (source is null)
                throw new ArgumentNullException(nameof(source));
            if (writer is null)
                throw new ArgumentNullException(nameof(writer));
            if (write is null)
                throw new ArgumentNullException(nameof(write));
            if (fields is null)
                throw new ArgumentNullException(nameof(fields));
            if (result is null)
                throw new ArgumentNullException(nameof(result));

            var affected = 0L;

            await foreach (var row in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var bytes = CosmosDocument.Build(write.ColumnNames, fields(row));

                using var document = JsonDocument.Parse(bytes);

                var partitionKey = PartitionKeyOf(document.RootElement, write);

                switch (write.Operation)
                {
                    case CosmosWriteOperation.Insert:
                        await writer.CreateItemAsync(bytes, partitionKey, cancellationToken).ConfigureAwait(false);
                        affected++;
                        break;

                    case CosmosWriteOperation.Delete:
                        {
                            if (CosmosDocument.Read(document.RootElement, "/" + Metadata.CosmosContainerMetadata.IdPropertyName) is not string id)
                                throw new CosmosExecutionException("A row being deleted carries no 'id', so the document it names cannot be identified.");

                            if (await writer.DeleteItemAsync(id, partitionKey, cancellationToken).ConfigureAwait(false))
                                affected++;

                            break;
                        }

                    case CosmosWriteOperation.Update:
                        {
                            // The document just built is the OLD one — the row's table columns hold
                            // what the scan read — and it is what identifies the target: the id, and
                            // the partition key, which a replace cannot change.
                            if (CosmosDocument.Read(document.RootElement, "/" + Metadata.CosmosContainerMetadata.IdPropertyName) is not string id)
                                throw new CosmosExecutionException("A row being updated carries no 'id', so the document it names cannot be identified.");

                            var replacement = CosmosDocument.Build(write.ColumnNames, ApplySets(write, fields(row)));

                            if (await writer.ReplaceItemAsync(replacement, id, partitionKey, cancellationToken).ConfigureAwait(false))
                                affected++;

                            break;
                        }

                    default:
                        throw new CosmosExecutionException($"No write is defined for operation '{write.Operation}'.");
                }
            }

            // The container changed, so what the lookup cache remembers about it may be wrong.
            // Cleared whatever was written: goodwill is cheap here, and a write from outside the
            // process is the TTL's problem rather than this line's.
            invalidate?.Clear();

            yield return result(affected);
        }

        /// <summary>
        /// Produces the values describing the replacement document: the old row with each
        /// <c>SET</c> column's value substituted from the trailing positions the planner appends.
        /// </summary>
        /// <remarks>
        /// Where the map column is itself being set, the other columns' old values are withheld
        /// rather than substituted: the document builder lets a non-null promoted column override
        /// the map's entry, which is right for an insert describing one document, and wrong here —
        /// it would silently write old values over whatever the new map says. The one deliberate
        /// exception is <c>id</c>: identity is not the statement's to change, and keeping the old
        /// value makes a new map that omits it still describe the same document — while a new map
        /// that <em>contradicts</em> it produces a body the service rejects loudly, which is the
        /// correct fate for an update trying to rename a document.
        /// </remarks>
        static object?[] ApplySets(CosmosWrite write, object?[] values)
        {
            var updates = write.UpdateColumnNames
                ?? throw new CosmosExecutionException("An update carries no SET columns.");

            var count = write.ColumnNames.Length;
            var updated = new object?[count];
            Array.Copy(values, updated, Math.Min(count, values.Length));

            var mapSet = Array.IndexOf(updates, CosmosImplementor.MapColumnName) >= 0;
            if (mapSet)
                for (var i = 0; i < count; i++)
                    if (i != CosmosImplementor.MapColumnOrdinal &&
                        Array.IndexOf(updates, write.ColumnNames[i]) < 0 &&
                        string.Equals(write.ColumnNames[i], Metadata.CosmosContainerMetadata.IdPropertyName, StringComparison.Ordinal) == false)
                        updated[i] = null;

            for (var j = 0; j < updates.Length; j++)
            {
                var ordinal = Array.IndexOf(write.ColumnNames, updates[j]);
                if (ordinal < 0)
                    throw new CosmosExecutionException($"The SET column '{updates[j]}' is not a column of the table.");

                if (count + j >= values.Length)
                    throw new CosmosExecutionException($"The row carries no value for the SET column '{updates[j]}'.");

                updated[ordinal] = values[count + j];
            }

            return updated;
        }

        /// <summary>
        /// Builds the partition key a document belongs to.
        /// </summary>
        /// <remarks>
        /// A declared path the document does not carry is <em>absent</em> rather than null, and Cosmos
        /// distinguishes the two: an absent key is the "none" logical partition, which is a real place
        /// documents live and not an error. <see cref="PartitionKeyBuilder.AddNoneType"/> is how the SDK
        /// names it, and passing null instead would route to a different partition.
        /// </remarks>
        /// <param name="document">The document.</param>
        /// <param name="write">The write, which carries the declared paths.</param>
        /// <returns>The partition key.</returns>
        static Microsoft.Azure.Cosmos.PartitionKey PartitionKeyOf(JsonElement document, CosmosWrite write)
        {
            var builder = new Microsoft.Azure.Cosmos.PartitionKeyBuilder();

            foreach (var path in write.PartitionKeyPaths)
            {
                if (CosmosDocument.Contains(document, path) == false)
                {
                    builder.AddNoneType();
                    continue;
                }

                switch (CosmosDocument.Read(document, path))
                {
                    case null:
                        builder.AddNullValue();
                        break;
                    case string s:
                        builder.Add(s);
                        break;
                    case bool b:
                        builder.Add(b);
                        break;
                    case double d:
                        builder.Add(d);
                        break;
                    default:
                        throw new CosmosExecutionException($"The partition key path '{path}' holds a value that cannot be a partition key.");
                }
            }

            return builder.Build();
        }

    }

}
