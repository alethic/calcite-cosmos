namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// What the service says about a container's size and shape, as against what its definition
    /// declares.
    /// </summary>
    /// <param name="DocumentCount">How many documents the container holds.</param>
    /// <param name="DocumentSizeInBytes">What they occupy in total.</param>
    /// <param name="PartitionCount">How many physical partitions the container is spread over.</param>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="CosmosContainerMetadata"/> because it is a different kind of fact.
    /// Everything there is <em>declared</em> — a partition key, an indexing policy — and is true until
    /// someone changes the container. These are measurements, and they were true when they were taken.
    /// </para>
    /// <para>
    /// <b>That is not a reason to distrust them.</b> A planner row count is allowed to be approximate
    /// and stale; what it must not be is invented, and none of this is. The rule the rest of the adapter
    /// follows — nothing inferred from sampling documents — is about the <em>shape</em> of the data,
    /// where a wrong guess yields an incorrect plan. A wrong row count yields a slow one.
    /// </para>
    /// <para>
    /// The count of partitions is the one with no substitute. A cross-partition query costs roughly
    /// that many single-partition queries, so it is the difference between pinning the partition key
    /// being worth a little and being worth nearly everything, and no amount of declared metadata says
    /// what it is.
    /// </para>
    /// </remarks>
    public readonly record struct CosmosContainerStatistics(long DocumentCount, long DocumentSizeInBytes, int PartitionCount)
    {

        /// <summary>
        /// Gets the average size of a document, or zero where the container is empty.
        /// </summary>
        /// <remarks>
        /// What a row costs to move, which for a row model carrying whole documents is the term that
        /// dominates everything else.
        /// </remarks>
        public double AverageDocumentSizeInBytes => DocumentCount > 0 ? (double)DocumentSizeInBytes / DocumentCount : 0d;

    }

}
