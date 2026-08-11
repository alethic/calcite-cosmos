using System;

using org.apache.calcite;
using org.apache.calcite.schema;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Finds the table a compiled plan reads from, in the schema that plan is being executed against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A converter knows which table it is reading at planning time, but must not write that table into
    /// the plan: Calcite prepares a statement once and executes it many times, so what the plan holds has
    /// to be a route rather than a resource. The route is the table's qualified name, and this walks it
    /// from the <see cref="DataContext"/>'s root schema each time the plan runs.
    /// </para>
    /// <para>
    /// The counterpart of what an adapter does with <c>Schemas.unwrap</c> over a convention's schema
    /// expression; spelled out here because the plan this adapter builds is a
    /// <see cref="System.Linq.Expressions"/> tree and reaches its schema by calling into this rather than
    /// by carrying a linq4j expression it would have to translate.
    /// </para>
    /// </remarks>
    public static class CosmosSchemas
    {

        /// <summary>
        /// Returns what executes statements against the named table.
        /// </summary>
        /// <param name="root">The context the query is executing against.</param>
        /// <param name="qualifiedName">The table's qualified name, schemas outermost first.</param>
        /// <returns>The executor.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="qualifiedName"/> is <c>null</c>.</exception>
        /// <exception cref="CosmosExecutionException">The table is not reachable, or has no executor.</exception>
        public static ICosmosQueryExecutor GetExecutor(DataContext root, string[] qualifiedName)
        {
            var cosmos = GetTable(root, qualifiedName);

            // A table built from metadata alone plans perfectly well and cannot be read from. This is
            // where that shows, and it is the first point at which it could have.
            return cosmos.Executor
                ?? throw new CosmosExecutionException($"Table '{string.Join(".", qualifiedName)}' has no query executor, so its rows cannot be read. It was created from container metadata alone, without a Cosmos client.");
        }

        /// <summary>
        /// Returns the named table's lookup cache across executions, or <c>null</c> where none is
        /// configured — which unlike a missing executor is not an error, a cache being an option.
        /// </summary>
        /// <param name="root">The context the query is executing against.</param>
        /// <param name="qualifiedName">The table's qualified name, schemas outermost first.</param>
        /// <returns>The cache, or <c>null</c>.</returns>
        public static CosmosLookupCache? GetLookupCache(DataContext root, string[] qualifiedName)
        {
            return GetTable(root, qualifiedName).LookupCache;
        }

        /// <summary>
        /// Returns something that counts the documents in one logical partition of the named table.
        /// </summary>
        /// <remarks>
        /// What a whole-partition <c>DELETE</c> answers with. The operation itself reports no count,
        /// so this is asked first, and it is as racy against a concurrent writer as the scan the
        /// fast path replaces — no more, and no less.
        /// </remarks>
        /// <param name="root">The context the statement is executing against.</param>
        /// <param name="qualifiedName">The table's qualified name, schemas outermost first.</param>
        /// <returns>The counter.</returns>
        public static Func<Microsoft.Azure.Cosmos.PartitionKey, System.Threading.CancellationToken, System.Threading.Tasks.Task<long>> GetPartitionCounter(DataContext root, string[] qualifiedName)
        {
            var executor = GetExecutor(root, qualifiedName);

            return async (partitionKey, cancellationToken) =>
            {
                var query = new CosmosQuery("SELECT VALUE COUNT(1) FROM c", System.Array.Empty<Sql.CosmosParameter>());

                await foreach (var element in executor.ExecuteAsync(query, partitionKey, cancellationToken))
                    return element.ValueKind == System.Text.Json.JsonValueKind.Number ? element.GetInt64() : 0L;

                return 0L;
            };
        }

        static CosmosTable GetTable(DataContext root, string[] qualifiedName)
        {
            if (root is null)
                throw new ArgumentNullException(nameof(root));
            if (qualifiedName is null)
                throw new ArgumentNullException(nameof(qualifiedName));
            if (qualifiedName.Length == 0)
                throw new CosmosExecutionException("A table reference carries no name.");

            var name = string.Join(".", qualifiedName);

            SchemaPlus schema = root.getRootSchema()
                ?? throw new CosmosExecutionException($"Table '{name}' cannot be reached: the context has no root schema.");

            // Everything but the last element names a schema; the last names the table. Both are resolved
            // through Lookup rather than the deprecated getSubSchema/getTable, which cannot express that
            // this is a case-sensitive lookup — and it is: the name came from the plan, which got it from
            // the validator, so it is already the name the schema registered.
            for (var i = 0; i < qualifiedName.Length - 1; i++)
            {
                schema = (SchemaPlus?)schema.subSchemas().get(qualifiedName[i])
                    ?? throw new CosmosExecutionException($"Table '{name}' cannot be reached: schema '{qualifiedName[i]}' is not in the root schema.");
            }

            var table = (Table?)schema.tables().get(qualifiedName[qualifiedName.Length - 1])
                ?? throw new CosmosExecutionException($"Table '{name}' is not in its schema.");

            if (table is not CosmosTable cosmos)
                throw new CosmosExecutionException($"Table '{name}' resolved to a {table.GetType().Name} rather than a {nameof(CosmosTable)}.");

            return cosmos;
        }

        /// <summary>
        /// Returns what writes documents to the named table.
        /// </summary>
        /// <remarks>
        /// The same walk as <see cref="GetExecutor"/>, asking for the other capability. They are separate
        /// interfaces, so a table may be readable and not writable — an executor supplied by a caller who
        /// implemented only <see cref="ICosmosQueryExecutor"/> is exactly that, and this is where it says
        /// so rather than failing on the first document.
        /// </remarks>
        /// <param name="root">The context the statement is executing against.</param>
        /// <param name="qualifiedName">The table's qualified name, schemas outermost first.</param>
        /// <returns>The writer.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="root"/> or <paramref name="qualifiedName"/> is <c>null</c>.</exception>
        /// <exception cref="CosmosExecutionException">The table is not reachable, or cannot be written to.</exception>
        public static ICosmosItemWriter GetWriter(DataContext root, string[] qualifiedName)
        {
            var executor = GetExecutor(root, qualifiedName);

            return executor as ICosmosItemWriter
                ?? throw new CosmosExecutionException($"Table '{string.Join(".", qualifiedName)}' has a query executor that cannot write documents: {executor.GetType().Name} does not implement {nameof(ICosmosItemWriter)}.");
        }

    }

}
