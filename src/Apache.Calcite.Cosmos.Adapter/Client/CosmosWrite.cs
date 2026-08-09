using System;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// What a write does to a document.
    /// </summary>
    public enum CosmosWriteOperation
    {

        /// <summary>
        /// Creates a document, failing where one with that <c>id</c> already exists in the partition.
        /// </summary>
        /// <remarks>
        /// Create rather than upsert, because that is what <c>INSERT</c> means. An upsert would make
        /// <c>INSERT</c> silently replace, which SQL reserves for <c>MERGE</c>; the service answers a
        /// duplicate with a conflict and the statement fails, which is the answer the caller asked for.
        /// </remarks>
        Insert,

        /// <summary>
        /// Deletes the document a row identifies.
        /// </summary>
        Delete,

        /// <summary>
        /// Replaces the document a row identifies with the one the row's <c>SET</c> values describe.
        /// </summary>
        /// <remarks>
        /// A whole-document write, because that is what the statement says: SQL assigns whole values
        /// to named columns, and the settable column of this row model is the map column, which is
        /// the document. Carrying a targeted <c>SET</c> as a patch instead is a different operation
        /// with its own conditions — see <c>DESIGN.md</c> under <em>Updating</em> — and is not yet
        /// taken.
        /// </remarks>
        Update,

    }

    /// <summary>
    /// Everything a write needs that is known while the plan is being built.
    /// </summary>
    /// <remarks>
    /// The write path's counterpart to <c>CosmosQuery</c>, and deliberately not one: there is no
    /// statement here, because Cosmos SQL has no DML and a write goes through item CRUD instead. What
    /// is constant-folded into the plan is a description of the rows, not text.
    /// </remarks>
    /// <param name="Operation">What to do with each row.</param>
    /// <param name="ColumnNames">The table's field names, the map column first. An update's input rows carry one further value per <paramref name="UpdateColumnNames"/> entry, after these.</param>
    /// <param name="PartitionKeyPaths">The container's declared partition key paths, in policy form and outermost first.</param>
    /// <param name="UpdateColumnNames">The columns an <c>UPDATE</c> sets, in the order their new values trail the row, or <c>null</c> for any other operation.</param>
    public sealed record CosmosWrite(CosmosWriteOperation Operation, string[] ColumnNames, string[] PartitionKeyPaths, string[]? UpdateColumnNames = null)
    {

        /// <summary>
        /// Gets the table's field names, the map column first.
        /// </summary>
        public string[] ColumnNames { get; } = ColumnNames ?? throw new ArgumentNullException(nameof(ColumnNames));

        /// <summary>
        /// Gets the container's declared partition key paths, in policy form and outermost first.
        /// </summary>
        public string[] PartitionKeyPaths { get; } = PartitionKeyPaths ?? throw new ArgumentNullException(nameof(PartitionKeyPaths));

    }

}
