using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Apache.Calcite.Cosmos.Adapter.Sql;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// An <see cref="ICosmosQueryExecutor"/> bound to a single container.
    /// </summary>
    /// <remarks>
    /// Also an <see cref="ICosmosItemWriter"/>. The two are separate interfaces because they are
    /// separate capabilities, and one class because they are the same container.
    /// </remarks>
    public sealed class CosmosQueryExecutor : ICosmosQueryExecutor, ICosmosItemWriter
    {

        readonly Container _container;
        readonly bool _indexMetrics;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container to execute against.</param>
        /// <param name="indexMetrics">
        /// Whether to ask the service which indexes each statement used. Off by default: the service
        /// computes it per query and it is a diagnostic rather than something the adapter acts on.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public CosmosQueryExecutor(Container container, bool indexMetrics = false)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
            _indexMetrics = indexMetrics;
        }

        /// <summary>
        /// Records what a response cost.
        /// </summary>
        /// <remarks>
        /// Per response rather than per execution: a query spanning continuations is charged per page.
        /// </remarks>
        void Report(double charge, string kind)
        {
            var container = new KeyValuePair<string, object?>("cosmos.container", _container.Id);
            var which = new KeyValuePair<string, object?>("cosmos.request_kind", kind);

            CosmosInstrumentation.RequestCharge.Record(charge, container, which);
            CosmosInstrumentation.Responses.Add(1, container, which);
        }

        /// <summary>
        /// Reads one document directly, yielding it if it exists and nothing if it does not.
        /// </summary>
        /// <remarks>
        /// A missing document is an empty result rather than an error: the query this stands in for
        /// would have returned no rows, and a read that answers "no such document" is that answer.
        /// </remarks>
        async IAsyncEnumerable<JsonElement> ReadItemAsync(string id, PartitionKey partitionKey, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            using var response = await _container.ReadItemStreamAsync(id, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.PointRead);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                yield break;

            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Cloned for the same reason the query path clones: the element belongs to the document,
            // which is disposed here and returns its buffer to the pool.
            yield return document.RootElement.Clone();
        }

        /// <summary>
        /// Reads a set of documents directly, yielding those that exist.
        /// </summary>
        /// <remarks>
        /// A missing id is simply absent from the result, which is the answer the query this stands
        /// in for would have given — the same stance the single read takes on a 404.
        /// </remarks>
        async IAsyncEnumerable<JsonElement> ReadManyAsync(IReadOnlyList<string> ids, PartitionKey partitionKey, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var items = new List<(string, PartitionKey)>(ids.Count);
            foreach (var id in ids)
                items.Add((id, partitionKey));

            using var response = await _container.ReadManyItemsStreamAsync(items, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.PointRead);

            response.EnsureSuccessStatusCode();

            using var document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);

            // The same envelope a query page carries, and cloned for the same reason: the elements
            // belong to the document, which returns its buffer to the pool here.
            foreach (var element in document.RootElement.GetProperty("Documents").EnumerateArray())
                yield return element.Clone();
        }

        /// <summary>
        /// Builds the SDK query definition for a rendered statement.
        /// </summary>
        /// <param name="query">The statement and its bound parameters.</param>
        /// <returns>The query definition.</returns>
        public static QueryDefinition CreateDefinition(CosmosQuery query)
        {
            var definition = new QueryDefinition(query.Sql);

            foreach (var parameter in query.Parameters)
                definition = definition.WithParameter(parameter.Name, parameter.Value);

            return definition;
        }

        /// <summary>
        /// Builds a partition key from the values a predicate pinned.
        /// </summary>
        /// <remarks>
        /// Cosmos types partition key components, so each value is added by its JSON type. Anything
        /// else — an array or an object — cannot be a partition key, and yields no key rather than
        /// a wrong one.
        /// </remarks>
        /// <param name="values">One value per declared partition key path, in order.</param>
        /// <returns>The partition key, or <c>null</c> if the values cannot form one.</returns>
        public static PartitionKey? CreatePartitionKey(IReadOnlyList<object?>? values)
        {
            if (values is null || values.Count == 0)
                return null;

            var builder = new PartitionKeyBuilder();

            foreach (var value in values)
            {
                switch (value)
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
                    case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                        builder.Add(Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    default:
                        return null;
                }
            }

            return builder.Build();
        }

        /// <summary>
        /// Builds the request options for a statement.
        /// </summary>
        /// <remarks>
        /// A separate method because it is the only part of executing a statement that can be checked
        /// without a service, and each of the three things it decides is a decision rather than a
        /// default.
        /// </remarks>
        /// <param name="query">The statement being executed.</param>
        /// <param name="partitionKey">The partition key the statement resolved to, where it resolved to one.</param>
        /// <param name="indexMetrics">Whether to ask which indexes the statement used.</param>
        /// <returns>The options.</returns>
        public static QueryRequestOptions CreateRequestOptions(CosmosQuery query, PartitionKey? partitionKey, bool indexMetrics = false)
        {
            var options = new QueryRequestOptions();

            if (partitionKey is PartitionKey key)
            {
                options.PartitionKey = key;

                // One logical partition lives on one physical partition, so there is nothing for a
                // second worker to read. The SDK otherwise sizes its fan-out for a query that might
                // span every partition, which is machinery this statement has no use for.
                options.MaxConcurrency = 1;
            }

            // A page size, not a limit. The statement already says how many rows it wants; this stops
            // the service filling a default-sized page with rows the statement would discard.
            if (query.MaxItemCount is int maxItemCount)
                options.MaxItemCount = maxItemCount;

            // Which indexes the statement used, where the caller asked. A diagnostic rather than
            // something acted on.
            options.PopulateIndexMetrics = indexMetrics;

            return options;
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<JsonElement> ExecuteAsync(CosmosQuery query, PartitionKey? partitionKey = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // An explicit key wins; otherwise use whatever the predicate pinned.
            var effective = partitionKey ?? CreatePartitionKey(query.PartitionKeyValues);

            // A statement that is exactly a lookup by id and a complete partition key is a read, not a
            // query: about 1 RU against 2.3 at best, and no query engine. The caller decided this is
            // such a statement; what arrives is the document rather than a projection, which is why the
            // row builder for this path differs.
            // The key must be complete: a prefix routes to a set of partitions and does not identify a
            // document, so ReadItem cannot use one.
            if (query.PointReadId is string id && query.PartitionKeyIsComplete && effective is PartitionKey readKey)
            {
                await foreach (var document in ReadItemAsync(id, readKey, cancellationToken))
                    yield return document;

                yield break;
            }

            // The same recovery for a set of ids: ReadManyItemsAsync is charged as point reads,
            // and is gated by the same completeness the single read is.
            if (query.PointReadIds is { Count: > 0 } ids && query.PartitionKeyIsComplete && effective is PartitionKey manyKey)
            {
                await foreach (var document in ReadManyAsync(ids, manyKey, cancellationToken))
                    yield return document;

                yield break;
            }

            var options = CreateRequestOptions(query, effective, _indexMetrics);

            // The stream iterator is used rather than the typed one so that results are read with
            // System.Text.Json. The SDK requires Newtonsoft.Json to be present, but nothing here
            // needs to go through it.
            using var activity = CosmosInstrumentation.ActivitySource.StartActivity("cosmos.query");
            activity?.SetTag("db.query.text", query.Sql);
            activity?.SetTag("cosmos.container", _container.Id);

            using var iterator = _container.GetItemQueryStreamIterator(CreateDefinition(query), requestOptions: options);

            // Accumulated across continuations. The per-response measurement is what a collector
            // aggregates; this is what a reader of one trace wants, which is what the whole statement
            // cost rather than what its third page did.
            var charge = 0d;
            var pages = 0;

            while (iterator.HasMoreResults)
            {
                pages++;

                cancellationToken.ThrowIfCancellationRequested();

                using var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);

                Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Query);
                charge += response.Headers.RequestCharge;

                response.EnsureSuccessStatusCode();

                // Reported on the span rather than as a measurement: it is a paragraph of text naming
                // the indexes the service considered, which is a thing to read and not a thing to
                // aggregate. Present only on the first page, and only where it was asked for.
                if (response.IndexMetrics is string metrics && metrics.Length > 0)
                    activity?.SetTag("cosmos.index_metrics", metrics);

                using var document = await JsonDocument.ParseAsync(response.Content, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.TryGetProperty("Documents", out var documents) == false)
                    continue;

                foreach (var element in documents.EnumerateArray())
                {
                    // Clone: the element is owned by the JsonDocument, which is disposed at the end
                    // of this iteration and returns its buffer to the pool.
                    yield return element.Clone();
                }
            }

            // Set at the end rather than incrementally: a span carries the totals it finished with, and
            // an abandoned enumeration is a span that never reaches here — which is itself the signal
            // that the caller stopped reading.
            activity?.SetTag("cosmos.request_charge", charge);
            activity?.SetTag("cosmos.pages", pages);
        }

        /// <inheritdoc />
        public async Task CreateItemAsync(byte[] document, PartitionKey partitionKey, CancellationToken cancellationToken = default)
        {
            if (document is null)
                throw new ArgumentNullException(nameof(document));

            using var stream = new System.IO.MemoryStream(document, writable: false);
            using var response = await _container.CreateItemStreamAsync(stream, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Write);

            // Surfaced rather than let through as a raw SDK exception, because a conflict is the one
            // failure a caller is likely to be handling deliberately: it is what INSERT means when a
            // document with that id is already in the partition.
            if (response.IsSuccessStatusCode == false)
                throw new CosmosExecutionException($"Creating a document in '{_container.Id}' failed with {(int)response.StatusCode} {response.StatusCode}. {response.ErrorMessage}".TrimEnd());
        }

        /// <inheritdoc />
        public async Task<bool> DeleteItemAsync(string id, PartitionKey partitionKey, CancellationToken cancellationToken = default)
        {
            if (id is null)
                throw new ArgumentNullException(nameof(id));

            using var response = await _container.DeleteItemStreamAsync(id, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Write);

            // Nothing to delete is not a failure. The rows were read before they were deleted, so a
            // document that is gone by the time the delete arrives was deleted by someone else — and
            // reporting a smaller count is the honest answer to that, rather than an error.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return false;

            if (response.IsSuccessStatusCode == false)
                throw new CosmosExecutionException($"Deleting document '{id}' from '{_container.Id}' failed with {(int)response.StatusCode} {response.StatusCode}. {response.ErrorMessage}".TrimEnd());

            return true;
        }

        /// <inheritdoc />
        public async Task<bool> ReplaceItemAsync(byte[] document, string id, PartitionKey partitionKey, CancellationToken cancellationToken = default)
        {
            if (document is null)
                throw new ArgumentNullException(nameof(document));
            if (id is null)
                throw new ArgumentNullException(nameof(id));

            using var stream = new System.IO.MemoryStream(document, writable: false);
            using var response = await _container.ReplaceItemStreamAsync(stream, id, partitionKey, cancellationToken: cancellationToken).ConfigureAwait(false);

            Report(response.Headers.RequestCharge, CosmosInstrumentation.Kinds.Write);

            // As for a delete: the rows were read before they were replaced, so a document that is
            // gone by the time the replace arrives was deleted by someone else, and a smaller count
            // is the honest answer rather than an error.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return false;

            if (response.IsSuccessStatusCode == false)
                throw new CosmosExecutionException($"Replacing document '{id}' in '{_container.Id}' failed with {(int)response.StatusCode} {response.StatusCode}. {response.ErrorMessage}".TrimEnd());

            return true;
        }

    }

}
