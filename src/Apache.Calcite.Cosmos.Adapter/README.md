# Apache.Calcite.Cosmos.Adapter

**Apache.Calcite.Cosmos.Adapter** lets [Apache Calcite](https://calcite.apache.org/) treat [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) containers as first-class relational schemas.

Rather than going through ADO.NET or JDBC, the adapter translates the relational plan into **Cosmos SQL** — the query dialect the Cosmos DB engine natively accepts — and executes it against the container.

## How it works

1. A Cosmos database is registered with Calcite as a schema, one table per container.
2. Calcite's planner converts as much of the plan as possible into the Cosmos calling convention (`CosmosConvention`).
3. Nodes in that convention are rendered to Cosmos SQL and executed by the Cosmos query engine.
4. Results leave the convention as an `IAsyncEnumerable`, into the `ClrAsyncEnumerableConvention` provided by [`Apache.Calcite.Extensions`](https://www.nuget.org/packages/Apache.Calcite.Extensions).
5. Anything Cosmos cannot express is executed in-process by Calcite, under that convention.

## Queries are asynchronous

A query over a Cosmos table plans **only** when the root is asked for in `ClrAsyncEnumerableConvention`.

This is a property of the service, not a limitation of the adapter. The Cosmos v3 SDK has no synchronous data-plane API — a page of results arrives only by awaiting `FeedIterator.ReadNextAsync` — so a synchronous plan could do nothing but block a thread for a network round trip per continuation. Rather than hide that behind an `IEnumerable`, the adapter offers only the asynchronous exit.

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

## Full text search

Cosmos has full text search and SQL does not, so the functions come from this adapter. Chain its operator table into the one the validator is built with:

```csharp
SqlOperatorTables.chain(SqlStdOperatorTable.instance(), CosmosOperators.Instance)
```

`FULLTEXTCONTAINS`, `FULLTEXTCONTAINSALL` and `FULLTEXTCONTAINSANY` are then usable in a `WHERE` clause and push down to the service. The first argument must be a property path.

Ranking works too. `ORDER BY FULLTEXTSCORE(c."_MAP"['name'], 'steel') FETCH FIRST 10 ROWS ONLY` becomes `ORDER BY RANK`, and `RRF(...)` fuses two scores for hybrid search. The score is never projected — the service forbids it — so it ranks the rows and does not appear in the result. See [DESIGN.md](DESIGN.md).

## What a query cost

Cosmos charges in request units and reports the charge on every response. The adapter records it, on a `Meter` and an `ActivitySource` both named `Apache.Calcite.Cosmos.Adapter`:

| | |
|---|---|
| `cosmos.request_charge` | Request units, one measurement per response |
| `cosmos.responses` | Responses received |
| `cosmos.query` (span) | One statement, first request to last page |

Both instruments are tagged with `cosmos.container` and with `cosmos.request_kind`, which is `query` or `point_read` — so a point read can be told from the query it replaced. Collect them however you already collect .NET telemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter("Apache.Calcite.Cosmos.Adapter"))
    .WithTracing(t => t.AddSource("Apache.Calcite.Cosmos.Adapter"));
```

Add `"indexMetrics": true` to the operand to have the service report which indexes each statement used; it lands on the span as `cosmos.index_metrics`. Off by default, because the service computes it per query.

## Status

Under development. Statement generation, container metadata, the schema and table layer, the scan/filter/project/sort/unnest/aggregate/rank nodes, and execution inside a Calcite plan are in place and tested. Every emitted statement form is executed against a live service, and the suite runs against a real account when `COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` name one — which the emulator is not a substitute for, it having been found to accept statements the service rejects and reject features the service implements. See [DESIGN.md](DESIGN.md), including its record of assumptions still to be settled.

## Further reading

- [Apache Calcite documentation](https://calcite.apache.org/docs/)
- [Calcite adapters overview](https://calcite.apache.org/docs/adapter.html)
- [Cosmos DB SQL query reference](https://learn.microsoft.com/azure/cosmos-db/nosql/query/getting-started)
- [Source repository](https://github.com/alethic/calcite-cosmos)

## License

Apache License 2.0.
