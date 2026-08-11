using System.Threading;
using System.Threading.Tasks;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Writes documents to a container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="ICosmosQueryExecutor"/> rather than added to it, because they are
    /// different capabilities and a deployment may have only the first. Something that can read a
    /// container and cannot write one is a coherent thing to hand the adapter, and a caller that never
    /// writes should not have to implement this to plan a query.
    /// </para>
    /// <para>
    /// Item-at-a-time, and streaming. The SDK's bulk mode is a client option rather than an API, so a
    /// client factory that sets <c>AllowBulkExecution</c> changes the throughput profile of these calls
    /// without any of them changing.
    /// </para>
    /// </remarks>
    public interface ICosmosItemWriter
    {

        /// <summary>
        /// Creates a document.
        /// </summary>
        /// <param name="document">The document, as UTF-8 JSON.</param>
        /// <param name="partitionKey">The partition key the document belongs to.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns>A task that completes when the document has been written.</returns>
        /// <exception cref="CosmosExecutionException">The service refused the document.</exception>
        Task CreateItemAsync(byte[] document, PartitionKey partitionKey, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes a document.
        /// </summary>
        /// <param name="id">The document's <c>id</c>.</param>
        /// <param name="partitionKey">The partition key the document belongs to.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns><c>true</c> if a document was deleted, <c>false</c> if there was none to delete.</returns>
        /// <exception cref="CosmosExecutionException">The service refused the request.</exception>
        Task<bool> DeleteItemAsync(string id, PartitionKey partitionKey, CancellationToken cancellationToken = default);

        /// <summary>
        /// Deletes every document in a logical partition, in one request.
        /// </summary>
        /// <remarks>
        /// The operation is a per-account preview and answers 400 where it is not enabled, so
        /// nothing calls this without asking first; see
        /// <c>CosmosContainerMetadata.SupportsPartitionKeyDelete</c>. Deletion proceeds in the
        /// background, but the service documents the effect as immediate — the documents stop
        /// appearing in queries and reads before the physical removal completes.
        /// </remarks>
        /// <param name="partitionKey">The logical partition to empty.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns><c>true</c> if the service accepted the operation.</returns>
        /// <exception cref="CosmosExecutionException">The service refused the request.</exception>
        Task<bool> DeletePartitionAsync(PartitionKey partitionKey, CancellationToken cancellationToken = default);

        /// <summary>
        /// Determines whether this container's account will accept a whole-partition delete.
        /// </summary>
        /// <remarks>
        /// Safe by construction: the operation is invoked against a partition key value nothing can
        /// be stored under, so it deletes nothing whichever answer comes back. What is being read
        /// is the refusal, not the effect.
        /// </remarks>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns><c>true</c> where the account accepts it.</returns>
        Task<bool> SupportsPartitionDeleteAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Replaces a document.
        /// </summary>
        /// <param name="document">The new document, as UTF-8 JSON.</param>
        /// <param name="id">The <c>id</c> of the document being replaced.</param>
        /// <param name="partitionKey">The partition key the document belongs to.</param>
        /// <param name="cancellationToken">Cancels the request.</param>
        /// <returns><c>true</c> if a document was replaced, <c>false</c> if there was none to replace.</returns>
        /// <exception cref="CosmosExecutionException">The service refused the request.</exception>
        Task<bool> ReplaceItemAsync(byte[] document, string id, PartitionKey partitionKey, CancellationToken cancellationToken = default);

    }

}
