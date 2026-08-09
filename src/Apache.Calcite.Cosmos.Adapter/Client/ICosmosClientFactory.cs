using System.Collections.Generic;

using Microsoft.Azure.Cosmos;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Supplies the <see cref="CosmosClient"/> a schema reads through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Name an implementation in the model's <c>clientFactory</c> operand and
    /// <see cref="CosmosSchemaFactory"/> will construct it and ask it for a client, instead of building
    /// one from an endpoint and a key.
    /// </para>
    /// <para>
    /// This is the seam for everything a connection string cannot say. An account reached with
    /// <c>TokenCredential</c> rather than a key; a client with a custom serializer, retry policy or
    /// preferred regions; a client owned by a dependency injection container and shared with the rest of
    /// an application rather than created per schema. A key in a model file is a key in a model file,
    /// and it is the only thing the built-in path can use.
    /// </para>
    /// <para>
    /// The implementation needs a public parameterless constructor. It is given the model's operand map,
    /// so anything else it needs can be declared alongside the factory name.
    /// </para>
    /// </remarks>
    public interface ICosmosClientFactory
    {

        /// <summary>
        /// Creates the client for a schema.
        /// </summary>
        /// <remarks>
        /// The returned client is held for the life of the schema, which is the life of the process —
        /// Calcite offers no disposal hook to release it on. A factory handing back a shared client is
        /// therefore doing the right thing, and one constructing a fresh client per call should expect
        /// it never to be disposed.
        /// </remarks>
        /// <param name="operand">The model's operand map, verbatim.</param>
        /// <returns>The client.</returns>
        CosmosClient Create(IReadOnlyDictionary<string, object?> operand);

    }

}
