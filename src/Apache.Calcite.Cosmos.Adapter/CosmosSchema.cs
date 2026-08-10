using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;

using org.apache.calcite.schema;
using org.apache.calcite.schema.impl;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A Cosmos database exposed to Calcite as a schema, with one table per container.
    /// </summary>
    /// <remarks>
    /// Containers must be supplied explicitly rather than discovered by sampling. A container's
    /// declared metadata — its partition key and indexing policy — is read from the container
    /// definition; its document shape is not knowable and is not guessed.
    /// </remarks>
    public class CosmosSchema : AbstractSchema
    {

        readonly java.util.Map _tables = new java.util.LinkedHashMap();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="containers">The containers to expose.</param>
        /// <param name="executorFactory">
        /// Returns what executes statements against a container. Omit it to expose tables that can be
        /// planned against but not read — which is what a schema built from metadata alone can offer.
        /// </param>
        /// <param name="lookupCacheFactory">
        /// Returns a container's lookup cache across executions, or <c>null</c> for none. One instance
        /// per container: the cache shares the schema's lifetime and its declared freshness policy.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="containers"/> is <c>null</c>.</exception>
        public CosmosSchema(
            IEnumerable<CosmosContainerMetadata> containers,
            Func<CosmosContainerMetadata, ICosmosQueryExecutor>? executorFactory = null,
            Func<CosmosContainerMetadata, CosmosLookupCache?>? lookupCacheFactory = null)
        {
            if (containers is null)
                throw new ArgumentNullException(nameof(containers));

            foreach (var container in containers)
                _tables.put(container.Name, new CosmosTable(container, executorFactory?.Invoke(container), lookupCacheFactory?.Invoke(container)));
        }

        /// <inheritdoc />
        protected override java.util.Map getTableMap()
        {
            return _tables;
        }

    }

}
