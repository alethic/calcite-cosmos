using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

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

}
