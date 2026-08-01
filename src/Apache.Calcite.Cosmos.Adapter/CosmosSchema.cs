using System;
using System.Collections.Generic;

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
        /// <exception cref="ArgumentNullException"><paramref name="containers"/> is <c>null</c>.</exception>
        public CosmosSchema(IEnumerable<CosmosContainerMetadata> containers)
        {
            if (containers is null)
                throw new ArgumentNullException(nameof(containers));

            foreach (var container in containers)
                _tables.put(container.Name, new CosmosTable(container));
        }

        /// <inheritdoc />
        protected override java.util.Map getTableMap()
        {
            return _tables;
        }

    }

}
