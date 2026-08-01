# Apache.Calcite.Cosmos.Adapter

**Apache.Calcite.Cosmos.Adapter** lets [Apache Calcite](https://calcite.apache.org/) treat [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) containers as first-class relational schemas.

Rather than going through ADO.NET or JDBC, the adapter translates the relational plan into **Cosmos SQL** — the query dialect the Cosmos DB engine natively accepts — and executes it against the container.

## How it works

1. A Cosmos container is registered with Calcite as a schema.
2. Calcite's planner converts as much of the plan as possible into the Cosmos calling convention (`CosmosConvention`).
3. Nodes in that convention are rendered to Cosmos SQL and executed by the Cosmos query engine.
4. Anything Cosmos cannot express is executed in-process by Calcite's enumerable runtime.

## Install

```sh
dotnet add package Apache.Calcite.Cosmos.Adapter
```

## Status

Early development. The convention and relational node contracts are in place; SQL generation and the schema/table implementations are being built out.

## Further reading

- [Apache Calcite documentation](https://calcite.apache.org/docs/)
- [Calcite adapters overview](https://calcite.apache.org/docs/adapter.html)
- [Cosmos DB SQL query reference](https://learn.microsoft.com/azure/cosmos-db/nosql/query/getting-started)
- [Source repository](https://github.com/alethic/calcite-cosmos)

## License

Apache License 2.0.
