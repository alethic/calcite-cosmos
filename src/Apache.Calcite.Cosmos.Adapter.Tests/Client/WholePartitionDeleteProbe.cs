using System;
using System.Text;
using System.Threading.Tasks;

using FluentAssertions;

using Microsoft.Azure.Cosmos;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests.Client
{

    /// <summary>
    /// Pins the measurement that blocks the whole-partition <c>DELETE</c>: the service refuses
    /// <c>DeleteAllItemsByPartitionKeyStreamAsync</c> where the account-level preview capability is
    /// not enrolled, and the emulator refuses it always.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asserted rather than merely recorded, per the rule that an unasserted gap goes unnoticed
    /// when it closes: <b>a failure here means the block has lifted</b> — the emulator or account
    /// under test now implements the operation — and the whole-partition <c>DELETE</c> item in
    /// <c>TODO.md</c> reopens, with two design questions waiting in it.
    /// </para>
    /// <para>
    /// Measured on the emulator: 400 BadRequest, and nothing deleted then or later. The preview is
    /// also not discoverable as a registrable feature under the test subscription, so a real
    /// account could not be measured either.
    /// </para>
    /// </remarks>
    [TestClass]
    public class WholePartitionDeleteProbe
    {

        const string EmulatorEndpoint = "http://localhost:8081/";
        const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        static readonly string Endpoint = Environment.GetEnvironmentVariable("COSMOS_TEST_ENDPOINT") is string e && e.Length > 0 ? e : EmulatorEndpoint;
        static readonly string Key = Environment.GetEnvironmentVariable("COSMOS_TEST_KEY") is string k && k.Length > 0 ? k : EmulatorKey;
        static bool IsEmulator => ReferenceEquals(Endpoint, EmulatorEndpoint);

        [TestMethod]
        public async Task DeleteAllItemsByPartitionKeyIsStillRefused()
        {
            var options = new CosmosClientOptions
            {
                ConnectionMode = ConnectionMode.Gateway,
                RequestTimeout = TimeSpan.FromSeconds(IsEmulator ? 5 : 30),
            };

            if (IsEmulator)
            {
                options.LimitToEndpoint = true;
                options.ServerCertificateCustomValidationCallback = (_, _, _) => true;
            }

            CosmosClient client;
            Database database;

            try
            {
                client = new CosmosClient(Endpoint, Key, options);
                database = (await client.CreateDatabaseIfNotExistsAsync("calcite_cosmos_wpd_probe")).Database;
            }
            catch (Exception)
            {
                Assert.Inconclusive("No Cosmos DB account reachable at " + Endpoint);
                return;
            }

            using (client)
            {
                try
                {
                    var container = (await database.CreateContainerIfNotExistsAsync(new ContainerProperties("probe", "/pk"))).Container;

                    using var stream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("""{"id":"d0","pk":"x"}"""));
                    using var created = await container.CreateItemStreamAsync(stream, new PartitionKey("x"));

                    using var response = await container.DeleteAllItemsByPartitionKeyStreamAsync(new PartitionKey("x"));

                    response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest,
                        "this account implements the whole-partition delete: the block on the TODO's whole-partition DELETE item has lifted, and the item reopens");

                    // And the refusal deleted nothing: the document is still there.
                    using var read = await container.ReadItemStreamAsync("d0", new PartitionKey("x"));
                    read.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
                }
                finally
                {
                    try { await database.DeleteAsync(); } catch (CosmosException) { }
                }
            }
        }

    }

}
