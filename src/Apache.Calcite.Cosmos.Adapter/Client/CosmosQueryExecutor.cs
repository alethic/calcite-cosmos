using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Sql;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// An <see cref="ICosmosQueryExecutor"/> bound to a single container.
    /// </summary>
    public sealed class CosmosQueryExecutor : ICosmosQueryExecutor
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

            var options = new QueryRequestOptions();
            if (effective is PartitionKey key)
                options.PartitionKey = key;

            // A page size, not a limit. The statement already says how many rows it wants; this stops
            // the service filling a default-sized page with rows the statement would discard.
            if (query.MaxItemCount is int maxItemCount)
                options.MaxItemCount = maxItemCount;

            // Which indexes the statement used, where the caller asked. A diagnostic rather than
            // something acted on — and the one way to settle by measurement whether the composite index
            // guard is describing the service or the documentation.
            options.PopulateIndexMetrics = _indexMetrics;

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

    }

}
