using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;

using org.apache.calcite.schema.impl;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A Cosmos account exposed to Calcite as a schema, with one subschema per database.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cosmos nests account → database → container, and Calcite nests schema → subschema → table, so
    /// the two line up: this is the account, a <see cref="CosmosSchema"/> is a database, and a
    /// <see cref="CosmosTable"/> is a container. A query then names a container the way it names any
    /// table in a nested schema — <c>"inventory"."products"</c>.
    /// </para>
    /// <para>
    /// The alternative, and what a model naming a single <c>database</c> still gets, is one schema per
    /// database. That is the right shape when a caller wants one database and nothing else; it is the
    /// wrong shape for an account, because each entry builds its own client and nothing can join
    /// across two of them without two connections.
    /// </para>
    /// <para>
    /// Metadata for every container in every database is read when the schema is built. A container's
    /// definition is small and Calcite asks for the whole map at once, but an account with many
    /// databases pays for all of them to reach one — see <c>TODO.md</c>.
    /// </para>
    /// </remarks>
    public class CosmosAccountSchema : AbstractSchema
    {

        readonly java.util.Map _subSchemas = new java.util.LinkedHashMap();

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="databases">The databases to expose, each with the containers it should carry.</param>
        /// <param name="executorFactory">
        /// Returns what executes statements against a container of a named database. Omit it to expose
        /// databases that can be planned against but not read.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="databases"/> is <c>null</c>.</exception>
        public CosmosAccountSchema(
            IEnumerable<KeyValuePair<string, IReadOnlyList<CosmosContainerMetadata>>> databases,
            Func<string, CosmosContainerMetadata, ICosmosQueryExecutor>? executorFactory = null)
        {
            if (databases is null)
                throw new ArgumentNullException(nameof(databases));

            foreach (var (name, containers) in databases)
            {
                var database = name;

                _subSchemas.put(database, new CosmosSchema(
                    containers,
                    executorFactory is null ? null : container => executorFactory(database, container)));
            }
        }

        /// <inheritdoc />
        protected override java.util.Map getSubSchemaMap()
        {
            return _subSchemas;
        }

    }

}
