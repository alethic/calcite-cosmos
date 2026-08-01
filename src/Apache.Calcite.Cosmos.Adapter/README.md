# Apache.Calcite.Cosmos.Adapter

**Apache.Calcite.Cosmos.Adapter** lets [Apache Calcite](https://calcite.apache.org/) treat [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) containers as first-class relational schemas.

Rather than going through ADO.NET or JDBC, the adapter translates the relational plan into **Cosmos SQL** — the query dialect the Cosmos DB engine natively accepts — and executes it against the container.

## How it works

1. A Cosmos database is registered with Calcite as a schema, one table per container.
2. Calcite's planner converts as much of the plan as possible into the Cosmos calling convention (`CosmosConvention`).
3. Nodes in that convention are rendered to Cosmos SQL and executed by the Cosmos query engine.
4. Anything Cosmos cannot express is executed in-process by Calcite's enumerable runtime.

A container has no row schema, so a table is modelled as one map column carrying the whole document, plus promoted scalar columns for paths the service guarantees or the container declares — `id`, `_ts`, `_etag`, and the partition key. Nothing is inferred from sampling documents.

## Install

```sh
dotnet add package Apache.Calcite.Cosmos.Adapter
```

## Register a database

```json
{
  "name": "COSMOS",
  "type": "custom",
  "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory",
  "operand": {
    "endpoint": "https://account.documents.azure.com:443/",
    "key": "…",
    "database": "inventory",
    "containers": [ "products", "orders" ]
  }
}
```

Omit `containers` to expose every container in the database.

## Pushdown

| Operator | Rendered as |
|---|---|
| Filter | `WHERE` |
| Project | `SELECT VALUE { … }` |
| Sort | `ORDER BY`, `OFFSET`/`LIMIT` |
| Array traversal | `JOIN alias IN path` |

Relational joins, `UNION`/`INTERSECT`/`EXCEPT`, and `HAVING` have no Cosmos equivalent and are evaluated in-process by Calcite. Multi-property `ORDER BY` is pushed down only when the container declares a matching composite index, since the service rejects it otherwise.

## Status

Under development. Statement generation, container metadata, the schema and table layer, and the scan/filter/project/sort/unnest nodes are in place and tested. Aggregation and result execution inside a Calcite plan are not yet wired up. See [DESIGN.md](DESIGN.md), including its record of assumptions that still need verifying against a real account.

## Further reading

- [Apache Calcite documentation](https://calcite.apache.org/docs/)
- [Calcite adapters overview](https://calcite.apache.org/docs/adapter.html)
- [Cosmos DB SQL query reference](https://learn.microsoft.com/azure/cosmos-db/nosql/query/getting-started)
- [Source repository](https://github.com/alethic/calcite-cosmos)

## License

Apache License 2.0.
