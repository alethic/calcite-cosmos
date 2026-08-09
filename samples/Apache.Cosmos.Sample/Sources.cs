using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Microsoft.Azure.Cosmos;

namespace Apache.Cosmos.Sample
{

    /// <summary>
    /// Puts both sources into a known state before anything is queried.
    /// </summary>
    /// <remarks>
    /// Everything is created if missing and left alone if not, so the sample can be run repeatedly and
    /// the second run is as interesting as the first.
    /// </remarks>
    static class Sources
    {

        public const string Database = "CalciteCosmosSample";

        /// <summary>The emulator's well-known credentials. Documented by Microsoft, and not a secret.</summary>
        public const string CosmosEndpoint = "http://localhost:8081/";
        public const string CosmosKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

        /// <summary>Where the CSV adapter looks. A directory of files is the whole of its configuration.</summary>
        public static string CsvDirectory => Path.Combine(AppContext.BaseDirectory, "suppliers");

        /// <summary>
        /// Writes the relational side: the supplier each product is bought from.
        /// </summary>
        /// <remarks>
        /// Deliberately the <em>small</em> side. It is read whole, and its product ids are what get
        /// pushed into Cosmos — so these three rows decide how much of the container is read.
        /// </remarks>
        public static int EnsureCsv()
        {
            Directory.CreateDirectory(CsvDirectory);

            var rows = new[]
            {
                "PRODUCT:string,SUPPLIER:string,LEAD_DAYS:int",
                "bike-1,Northwind Cycles,14",
                "bike-2,Northwind Cycles,21",
                "shoe-1,Contoso Footwear,7",
            };

            File.WriteAllLines(Path.Combine(CsvDirectory, "SUPPLIERS.csv"), rows);

            return rows.Length - 1;
        }

        /// <summary>
        /// Creates the Cosmos side: the product catalogue.
        /// </summary>
        /// <remarks>
        /// The large side, and the point of the exercise. It holds documents the supplier file has
        /// never heard of, and the join should not read them.
        /// </remarks>
        public static async Task<int> EnsureCosmosAsync()
        {
            using var client = new CosmosClient(CosmosEndpoint, CosmosKey, EmulatorOptions());

            var database = (await client.CreateDatabaseIfNotExistsAsync(Database)).Database;
            var container = (await database.CreateContainerIfNotExistsAsync("products", "/category")).Container;

            var documents = new List<string>
            {
                """{"id":"bike-1","category":"bikes","name":"Trail Blazer","price":1200}""",
                """{"id":"bike-2","category":"bikes","name":"Road Runner","price":3400}""",
                """{"id":"bike-3","category":"bikes","name":"Cobblestone","price":890}""",
                """{"id":"shoe-1","category":"shoes","name":"Sprint","price":80}""",
                """{"id":"shoe-2","category":"shoes","name":"Marathon","price":110}""",
                """{"id":"tent-1","category":"camping","name":"Basecamp","price":260}""",
            };

            foreach (var document in documents)
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document));
                using var parsed = System.Text.Json.JsonDocument.Parse(document);

                await container.UpsertItemStreamAsync(stream, new PartitionKey(parsed.RootElement.GetProperty("category").GetString()));
            }

            return documents.Count;
        }

        static CosmosClientOptions EmulatorOptions() => new()
        {
            ConnectionMode = ConnectionMode.Gateway,
            LimitToEndpoint = true,
            ServerCertificateCustomValidationCallback = (_, _, _) => true,
            RequestTimeout = TimeSpan.FromSeconds(10),
        };

        /// <summary>
        /// Reports what is missing, so a failure to connect reads as a setup step rather than a bug.
        /// </summary>
        public static async Task<string?> WhatIsMissingAsync()
        {
            try
            {
                using var client = new CosmosClient(CosmosEndpoint, CosmosKey, EmulatorOptions());
                await client.ReadAccountAsync();
            }
            catch (Exception e)
            {
                return "The Cosmos DB emulator is not reachable at " + CosmosEndpoint + "." + Environment.NewLine +
                       "  " + e.Message.Split('\n')[0] + Environment.NewLine + Environment.NewLine +
                       "  docker run -d -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview";
            }

            return null;
        }

    }

}
