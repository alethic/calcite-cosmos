using System;
using System.Collections.Generic;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Metadata;

using Azure.Core;
using Azure.Identity;

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
    /// <para>
    /// <b>Omit <c>database</c> to expose the account instead.</b> Cosmos nests account → database →
    /// container and Calcite nests schema → subschema → table, so the schema then carries one
    /// subschema per database and a query names a container as <c>"inventory"."products"</c>. One
    /// entry, one client, and a join across two databases; naming a database is the right shape when
    /// a caller wants one and nothing else.
    /// </para>
    /// <para>
    /// <b>Omit <c>key</c> to authenticate with Microsoft Entra ID.</b> The endpoint alone means "reach
    /// this account as whoever I am" — a managed identity in a cluster, the developer's signed-in
    /// tooling on a laptop — and the same model file serves both. <c>tenantId</c> and <c>clientId</c>
    /// narrow that where the ambient identity is ambiguous. The identity needs a Cosmos DB data plane
    /// role; a control-plane role that shows the account in the portal does not let it read a
    /// document.
    /// </para>
    /// <para>
    /// Supply <c>clientFactory</c> instead to reach an account any other way — a certificate, a
    /// bespoke token cache, or a client the application already owns.
    /// </para>
    /// </remarks>
    public class CosmosSchemaFactory : SchemaFactory
    {

        /// <summary>The operand naming the account endpoint.</summary>
        public const string EndpointOperand = "endpoint";

        /// <summary>
        /// The operand carrying the account key, or omitted to authenticate with Microsoft Entra ID.
        /// </summary>
        /// <remarks>
        /// Omitting it is the better default where the deployment allows it: a key is a bearer secret
        /// with full access to the account and no expiry, and it has to live somewhere a model file can
        /// read it. See <see cref="CreateClient"/>.
        /// </remarks>
        public const string KeyOperand = "key";

        /// <summary>
        /// The operand naming the tenant to authenticate against, where the ambient identity does not
        /// pick the right one.
        /// </summary>
        public const string TenantIdOperand = "tenantId";

        /// <summary>
        /// The operand naming the client id of a user-assigned managed identity.
        /// </summary>
        /// <remarks>
        /// Needed only where more than one identity is assigned, since with one there is nothing to
        /// choose between.
        /// </remarks>
        public const string ClientIdOperand = "clientId";

        /// <summary>The operand naming the database.</summary>
        public const string DatabaseOperand = "database";

        /// <summary>The operand listing the containers to expose. Omit to discover them.</summary>
        public const string ContainersOperand = "containers";

        /// <summary>The operand selecting the connection mode, <c>gateway</c> or <c>direct</c>.</summary>
        public const string ConnectionModeOperand = "connectionMode";

        /// <summary>
        /// The operand asking the service which indexes each statement used.
        /// </summary>
        /// <remarks>
        /// Off by default. The service computes the answer per query, so this is something to turn on
        /// while working out why a query is expensive rather than to leave on. It appears on the
        /// <c>cosmos.query</c> span — see <see cref="CosmosInstrumentation"/>.
        /// </remarks>
        public const string IndexMetricsOperand = "indexMetrics";

        /// <summary>
        /// The operand naming an <see cref="ICosmosClientFactory"/> to obtain the client from.
        /// </summary>
        /// <remarks>
        /// Supplied instead of <see cref="EndpointOperand"/> and <see cref="KeyOperand"/>, and the way
        /// to reach anything a connection string cannot say — Entra ID, a custom serializer or retry
        /// policy, or a client the application already owns.
        /// </remarks>
        public const string ClientFactoryOperand = "clientFactory";

        /// <inheritdoc />
        public Schema create(SchemaPlus parentSchema, string name, java.util.Map operand)
        {
            if (operand is null)
                throw new ArgumentNullException(nameof(operand));

            // The client is owned by the schema for the life of the process. Schemas are created
            // once per connection and Calcite offers no disposal hook to release it on.
            var client = CreateClient(operand);
            var containers = GetStrings(operand, ContainersOperand);
            var indexMetrics = GetBoolean(operand, IndexMetricsOperand);

            // Naming a database gives a schema whose tables are its containers. Omitting it gives the
            // account: one subschema per database, which is how Cosmos nests and how Calcite nests, and
            // which needs one client rather than one per database.
            if (GetString(operand, DatabaseOperand) is not string database || database.Length == 0)
                return CreateAccountSchema(client, containers, indexMetrics);

            var db = client.GetDatabase(database);

            // The same database the metadata was read from also executes the queries, so the client the
            // schema holds outlives this call. A container reference is a handle rather than a connection,
            // so binding one per table costs nothing.
            return new CosmosSchema(
                ReadContainers(db, containers),
                container => new CosmosQueryExecutor(db.GetContainer(container.Name), indexMetrics));
        }

        /// <summary>
        /// Builds the account-level schema, with one subschema per database.
        /// </summary>
        /// <remarks>
        /// The <c>containers</c> operand still applies, and applies to every database — which is only
        /// useful where they share container names. Omitting it, which is the ordinary case here,
        /// exposes every container of every database.
        /// </remarks>
        static Schema CreateAccountSchema(CosmosClient client, IReadOnlyList<string> containers, bool indexMetrics)
        {
            var databases = new List<KeyValuePair<string, IReadOnlyList<CosmosContainerMetadata>>>();

            using (var iterator = client.GetDatabaseQueryIterator<DatabaseProperties>())
            {
                while (iterator.HasMoreResults)
                {
                    foreach (var properties in iterator.ReadNextAsync(CancellationToken.None).GetAwaiter().GetResult())
                    {
                        var database = client.GetDatabase(properties.Id);
                        databases.Add(new(properties.Id, ReadContainers(database, containers)));
                    }
                }
            }

            return new CosmosAccountSchema(
                databases,
                (database, container) => new CosmosQueryExecutor(client.GetDatabase(database).GetContainer(container.Name), indexMetrics));
        }

        /// <summary>
        /// Obtains the client: from a named factory where the model gives one, and from the endpoint
        /// otherwise — with a key if one is given, and as the ambient identity if not.
        /// </summary>
        /// <remarks>
        /// A factory and an endpoint are exclusive by construction rather than by check: a factory
        /// decides everything about the client, so an endpoint alongside one would be a value nothing
        /// reads.
        /// </remarks>
        public static CosmosClient CreateClient(java.util.Map operand)
        {
            if (operand is null)
                throw new ArgumentNullException(nameof(operand));

            if (GetString(operand, ClientFactoryOperand) is string factoryName && factoryName.Length > 0)
                return CreateFromFactory(factoryName, operand);

            var endpoint = GetString(operand, EndpointOperand)
                ?? throw new ArgumentException($"Operand '{EndpointOperand}' is required unless '{ClientFactoryOperand}' is given.");

            var options = new CosmosClientOptions();
            if (string.Equals(GetString(operand, ConnectionModeOperand), "gateway", StringComparison.OrdinalIgnoreCase))
                options.ConnectionMode = ConnectionMode.Gateway;

            // A key where one is given, and an identity where none is. The absence is the request:
            // there is no sensible reading of "no key and no factory" other than "authenticate as
            // whoever I am", and requiring a second operand to say so would only be a way to get it
            // wrong.
            if (GetString(operand, KeyOperand) is string key && key.Length > 0)
                return new CosmosClient(endpoint, key, options);

            return new CosmosClient(endpoint, CreateCredential(operand), options);
        }

        /// <summary>
        /// Builds the credential used when no key is given.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="DefaultAzureCredential"/>, which tries the ways an application is normally
        /// identified in turn — environment variables, a workload or managed identity, the developer's
        /// signed-in tooling — so the same model file works on a laptop and in a cluster without
        /// saying which is which.
        /// </para>
        /// <para>
        /// Two things are worth knowing before this works. The identity needs a Cosmos DB **data
        /// plane** role assignment; the control-plane roles that let it see the account in the portal
        /// do not grant it the ability to read a document. And the built-in Data Reader role includes
        /// the metadata read this adapter performs on every container, which a hand-built role can
        /// easily omit.
        /// </para>
        /// <para>
        /// Anything this cannot express — a certificate, a bespoke token cache, a credential the
        /// application already holds — is what <see cref="ICosmosClientFactory"/> is for.
        /// </para>
        /// </remarks>
        /// <param name="operand">The model's operand map.</param>
        /// <returns>The credential.</returns>
        public static TokenCredential CreateCredential(java.util.Map operand)
        {
            if (operand is null)
                throw new ArgumentNullException(nameof(operand));

            var options = new DefaultAzureCredentialOptions();

            if (GetString(operand, TenantIdOperand) is string tenantId && tenantId.Length > 0)
                options.TenantId = tenantId;

            if (GetString(operand, ClientIdOperand) is string clientId && clientId.Length > 0)
                options.ManagedIdentityClientId = clientId;

            return new DefaultAzureCredential(options);
        }

        /// <summary>
        /// Constructs the named factory and asks it for a client.
        /// </summary>
        /// <remarks>
        /// Resolved the way a model names any type: by assembly-qualified or fully qualified name,
        /// searching the loaded assemblies for the latter. The failures are told apart deliberately —
        /// a name that resolves to nothing, to something that is not a factory, or to a factory that
        /// cannot be constructed are three different mistakes in a model file.
        /// </remarks>
        static CosmosClient CreateFromFactory(string factoryName, java.util.Map operand)
        {
            var type = Type.GetType(factoryName, throwOnError: false)
                ?? ResolveFromLoadedAssemblies(factoryName)
                ?? throw new ArgumentException($"Operand '{ClientFactoryOperand}' names the type '{factoryName}', which could not be found.");

            if (typeof(ICosmosClientFactory).IsAssignableFrom(type) == false)
                throw new ArgumentException($"'{factoryName}' does not implement {nameof(ICosmosClientFactory)}.");

            var factory = Activator.CreateInstance(type) as ICosmosClientFactory
                ?? throw new ArgumentException($"'{factoryName}' could not be constructed; it needs a public parameterless constructor.");

            return factory.Create(ToDictionary(operand))
                ?? throw new ArgumentException($"'{factoryName}' returned no client.");
        }

        static Type? ResolveFromLoadedAssemblies(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly.GetType(name, throwOnError: false) is Type found)
                    return found;

            return null;
        }

        /// <summary>
        /// Presents the operand map to a factory as something it can read without knowing about IKVM.
        /// </summary>
        static IReadOnlyDictionary<string, object?> ToDictionary(java.util.Map operand)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);

            var iterator = operand.entrySet().iterator();
            while (iterator.hasNext())
            {
                var entry = (java.util.Map.Entry)iterator.next();
                if (entry.getKey()?.ToString() is string key)
                    values[key] = entry.getValue();
            }

            return values;
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

        /// <summary>
        /// Reads a flag, absent meaning false.
        /// </summary>
        /// <remarks>
        /// A model may deliver a JSON boolean or the string that was written in the file, depending on
        /// how it was parsed; both are read.
        /// </remarks>
        static bool GetBoolean(java.util.Map operand, string name) => operand.get(name) switch
        {
            null => false,
            java.lang.Boolean value => value.booleanValue(),
            var other => bool.TryParse(other.ToString(), out var parsed) && parsed,
        };

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
