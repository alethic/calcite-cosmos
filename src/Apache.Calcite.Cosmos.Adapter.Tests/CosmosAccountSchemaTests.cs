using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;

using FluentAssertions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Apache.Calcite.Cosmos.Adapter.Tests
{

    /// <summary>
    /// The account level, where Cosmos's account → database → container lines up with Calcite's
    /// schema → subschema → table.
    /// </summary>
    [TestClass]
    public class CosmosAccountSchemaTests
    {

        static List<string> Names(org.apache.calcite.schema.lookup.Lookup lookup)
        {
            var names = new List<string>();
            var iterator = lookup.getNames(org.apache.calcite.schema.lookup.LikePattern.any()).iterator();
            while (iterator.hasNext())
                names.Add((string)iterator.next());

            return names;
        }

        static CosmosAccountSchema Account() => new(
        [
            new("inventory", new[] { new CosmosContainerMetadata("products", new[] { "/category" }) }),
            new("sales", new[] { new CosmosContainerMetadata("orders"), new CosmosContainerMetadata("returns") }),
        ]);

        [TestMethod]
        public void EachDatabaseIsASubschema()
        {
            org.apache.calcite.schema.Schema account = Account();

            Names(account.subSchemas()).Should().BeEquivalentTo("inventory", "sales");
        }

        [TestMethod]
        public void ASubschemaCarriesItsOwnContainers()
        {
            org.apache.calcite.schema.Schema account = Account();

            var sales = (org.apache.calcite.schema.Schema)account.subSchemas().get("sales");
            Names(sales.tables()).Should().BeEquivalentTo("orders", "returns");
        }

        /// <remarks>
        /// A container reached through the account is the same kind of table as one reached through a
        /// database schema, carrying the same declared metadata — the nesting is where it is found, not
        /// what it is.
        /// </remarks>
        [TestMethod]
        public void AContainerKeepsItsMetadataThroughTheNesting()
        {
            org.apache.calcite.schema.Schema account = Account();

            var inventory = (org.apache.calcite.schema.Schema)account.subSchemas().get("inventory");
            var products = (CosmosTable)inventory.tables().get("products");

            products.Container.PartitionKeyPaths.Should().Equal("/category");
        }

        /// <remarks>
        /// A database schema built without a client plans and cannot read, and that survives the extra
        /// level: the account passes no executor down, so its tables have none either.
        /// </remarks>
        [TestMethod]
        public void WithoutAnExecutorFactoryTheTablesHaveNone()
        {
            org.apache.calcite.schema.Schema account = Account();

            var inventory = (org.apache.calcite.schema.Schema)account.subSchemas().get("inventory");
            ((CosmosTable)inventory.tables().get("products")).Executor.Should().BeNull();
        }

    }

}
