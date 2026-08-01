using System;
using System.Collections.Generic;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using Microsoft.Azure.Cosmos;

using org.apache.calcite.schema;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// Creates a <see cref="CosmosSchema"/> from a Calcite JSON model.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Register a Cosmos database in a model with:
    /// </para>
    /// <code>
    /// {
    ///   "name": "COSMOS",
    ///   "type": "custom",
    ///   "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory",
    ///   "operand": {
    ///     "endpoint": "https://account.documents.azure.com:443/",
    ///     "key": "…",
    ///     "database": "inventory",
    ///     "containers": [ "products", "orders" ]
    ///   }
    /// }
    /// </code>
    /// <para>
    /// Containers may be listed explicitly or discovered from the database. Either way each
    /// container's metadata is read from its definition — the partition key and indexing policy —
    /// and never inferred from the documents it holds.
    /// </para>
    /// </remarks>
    public class CosmosSchemaFactory : SchemaFactory
    {

        /// <summary>The operand naming the account endpoint.</summary>
        public const string EndpointOperand = "endpoint";

        /// <summary>The operand carrying the account key.</summary>
        public const string KeyOperand = "key";

        /// <summary>The operand naming the database.</summary>
        public const string DatabaseOperand = "database";

        /// <summary>The operand listing the containers to expose. Omit to discover them.</summary>
        public const string ContainersOperand = "containers";

        /// <summary>The operand selecting the connection mode, <c>gateway</c> or <c>direct</c>.</summary>
        public const string ConnectionModeOperand = "connectionMode";

        /// <inheritdoc />
        public Schema create(SchemaPlus parentSchema, string name, java.util.Map operand)
        {
            if (operand is null)
                throw new ArgumentNullException(nameof(operand));

            var endpoint = GetString(operand, EndpointOperand) ?? throw new ArgumentException($"Operand '{EndpointOperand}' is required.");
            var key = GetString(operand, KeyOperand) ?? throw new ArgumentException($"Operand '{KeyOperand}' is required.");
            var database = GetString(operand, DatabaseOperand) ?? throw new ArgumentException($"Operand '{DatabaseOperand}' is required.");

            var options = new CosmosClientOptions();
            if (string.Equals(GetString(operand, ConnectionModeOperand), "gateway", StringComparison.OrdinalIgnoreCase))
                options.ConnectionMode = ConnectionMode.Gateway;

            // The client is owned by the schema for the life of the process. Schemas are created
            // once per connection and Calcite offers no disposal hook to release it on.
            var client = new CosmosClient(endpoint, key, options);

            return new CosmosSchema(ReadContainers(client.GetDatabase(database), GetStrings(operand, ContainersOperand)));
        }

        /// <summary>
        /// Reads metadata for the named containers, or for every container in the database when
        /// none are named.
        /// </summary>
        static IReadOnlyList<CosmosContainerMetadata> ReadContainers(Database database, IReadOnlyList<string> names)
        {
            var containers = new List<CosmosContainerMetadata>();

            if (names.Count > 0)
            {
                foreach (var container in names)
                    containers.Add(CosmosContainerMetadataReader.ReadAsync(database.GetContainer(container), CancellationToken.None).GetAwaiter().GetResult());

                return containers;
            }

            using var iterator = database.GetContainerQueryIterator<ContainerProperties>();
            while (iterator.HasMoreResults)
                foreach (var properties in iterator.ReadNextAsync(CancellationToken.None).GetAwaiter().GetResult())
                    containers.Add(CosmosContainerMetadataReader.FromProperties(properties));

            return containers;
        }

        static string? GetString(java.util.Map operand, string name) => operand.get(name)?.ToString();

        static IReadOnlyList<string> GetStrings(java.util.Map operand, string name)
        {
            var values = new List<string>();

            switch (operand.get(name))
            {
                case null:
                    break;
                case java.util.List list:
                    for (var i = 0; i < list.size(); i++)
                        if (list.get(i)?.ToString() is string value && value.Length > 0)
                            values.Add(value);
                    break;
                case var single:
                    foreach (var value in single.ToString()!.Split(','))
                        if (value.Trim().Length > 0)
                            values.Add(value.Trim());
                    break;
            }

            return values;
        }

    }

}
