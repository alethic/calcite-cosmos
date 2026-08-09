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
    /// Executes a rendered Cosmos SQL statement and yields the resulting JSON values.
    /// </summary>
    /// <remarks>
    /// This is the seam between the adapter and the Cosmos SDK. Everything above it — planning,
    /// translation, statement assembly — is independent of the service, which is what allows it to
    /// be tested without a client or a network.
    /// </remarks>
    public interface ICosmosQueryExecutor
    {

        /// <summary>
        /// Executes a query and streams its results.
        /// </summary>
        /// <param name="query">The statement and its bound parameters.</param>
        /// <param name="partitionKey">Restricts execution to a single logical partition when known, avoiding a fan-out across every physical partition.</param>
        /// <param name="cancellationToken">Cancels the enumeration.</param>
        /// <returns>The result values, one per row.</returns>
        IAsyncEnumerable<JsonElement> ExecuteAsync(CosmosQuery query, PartitionKey? partitionKey = null, CancellationToken cancellationToken = default);

    }

    /// <summary>
    /// An <see cref="ICosmosQueryExecutor"/> bound to a single container.
    /// </summary>
    public sealed class CosmosQueryExecutor : ICosmosQueryExecutor
    {

        readonly Container _container;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container to execute against.</param>
        /// <exception cref="ArgumentNullException"><paramref name="container"/> is <c>null</c>.</exception>
        public CosmosQueryExecutor(Container container)
        {
            _container = container ?? throw new ArgumentNullException(nameof(container));
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

            var options = new QueryRequestOptions();
            if (effective is PartitionKey key)
                options.PartitionKey = key;

            // A page size, not a limit. The statement already says how many rows it wants; this stops
            // the service filling a default-sized page with rows the statement would discard.
            if (query.MaxItemCount is int maxItemCount)
                options.MaxItemCount = maxItemCount;

            // The stream iterator is used rather than the typed one so that results are read with
            // System.Text.Json. The SDK requires Newtonsoft.Json to be present, but nothing here
            // needs to go through it.
            using var iterator = _container.GetItemQueryStreamIterator(CreateDefinition(query), requestOptions: options);

            while (iterator.HasMoreResults)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var response = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

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
        }

    }

}
