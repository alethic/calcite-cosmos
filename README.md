# Apache Calcite Cosmos Adapter

Query [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) with SQL, through
[Apache Calcite](https://calcite.apache.org/), from .NET.

Containers become relational tables. As much of each query as Cosmos can evaluate is translated to
**Cosmos SQL** and executed by the service; whatever it cannot — joins, set operations, `HAVING` —
Calcite evaluates in-process over the rows that come back. Calcite itself runs in-process via
[IKVM](https://github.com/ikvmnet/ikvm): no JDBC, no Avatica, no second process.

```sh
dotnet add package Apache.Calcite.Cosmos.Adapter
dotnet add package Apache.Calcite.Data
```

## Querying a container

`Apache.Calcite.Data` is the ADO.NET provider. Point its `Model` at a JSON model that registers the
container as a schema, and query it with `DbCommand`.

```csharp
using System.Data.Common;
using Apache.Calcite.Data;

const string model = """
{
  "version": "1.0",
  "defaultSchema": "COSMOS",
  "schemas": [{
    "name": "COSMOS",
    "type": "custom",
    "factory": "Apache.Calcite.Cosmos.Adapter.CosmosSchemaFactory",
    "operand": {
      "endpoint": "https://account.documents.azure.com:443/",
      "key": "…",
      "database": "inventory",
      "containers": [ "products" ]
    }
  }]
}
""";

await using var connection = new CalciteConnection(new CalciteConnectionStringBuilder
{
    Model = "inline:" + model,
    CaseSensitive = true,
}.ConnectionString);

await connection.OpenAsync();

await using var command = connection.CreateCommand();
command.CommandText = """SELECT c."id", c."category" FROM "products" AS c WHERE c."category" = 'bikes'""";

await using var reader = await command.ExecuteReaderAsync();
while (await reader.ReadAsync())
    Console.WriteLine($"{reader.GetString(0)} {reader.GetString(1)}");
```

Omit `containers` to expose every container in the database.

## Use the asynchronous methods

**`ExecuteReaderAsync` and `ReadAsync`, not `ExecuteReader` and `Read`.** A query over a Cosmos table
plans only in the asynchronous calling convention, and `ExecuteReader` asks for a synchronous plan,
which will not be found.

This follows from the service rather than from the adapter. The Cosmos SDK has no synchronous
data-plane API — a page of results arrives only by awaiting it — so a synchronous plan could do
nothing but block a thread for a network round trip per page. Rather than hide that behind an
interface that looks cheap, the adapter offers only the asynchronous route.

## The row model

A container has no row schema: two items may share nothing but `id`. So a table is **one map column
holding the whole document**, named `_MAP`, plus promoted scalar columns for the paths the service
guarantees or the container declares — `id`, `_ts`, `_etag`, and the partition key. Nothing is
inferred by sampling documents, because a wrong guess yields an incorrect plan rather than a slow one.

Reach anything else through the map column, to any depth:

```sql
SELECT c."_MAP"['metadata']['sku'] AS "sku"
FROM "products" AS c
WHERE c."_MAP"['tags'][0] = 'steel'
```

Those collapse to the Cosmos paths `c.metadata.sku` and `c.tags[0]` and are evaluated by the service.
The key must be a constant — a Cosmos path names a property statically.

## What gets pushed down

| | |
|---|---|
| Filters | `WHERE`, including partial predicates — the renderable conjuncts push and the rest are rechecked in-process |
| Projections | `SELECT VALUE { … }` |
| Sorts, limits | `ORDER BY`, `OFFSET`/`LIMIT`; a multi-key sort only where a matching composite index is declared |
| Aggregation | `GROUP BY` with `COUNT`, `SUM`, `MIN`, `MAX`, `AVG` |
| Array traversal | `JOIN alias IN path` |
| Scalar functions | string, numeric and trigonometric functions where SQL and Cosmos agree on meaning |
| Partition key | recovered from the predicate, so execution stays on one physical partition |
| Row limits | a `FETCH` becomes the page size, so a bounded query stops paying for a full page |

Relational joins, `UNION`/`INTERSECT`/`EXCEPT` and `HAVING` have no Cosmos equivalent and run
in-process. Anything the adapter cannot render faithfully it declines rather than approximating.

## Full text search

Cosmos has full text search and SQL does not, so the functions — `FULLTEXTCONTAINS`,
`FULLTEXTSCORE`, `RRF` and the `IS_DEFINED` family — come from this package as Calcite operators, in
`CosmosOperators.Instance`. Chain that into the operator table the validator is built with:

```csharp
SqlOperatorTables.chain(SqlStdOperatorTable.instance(), CosmosOperators.Instance)
```

Ordering by a score becomes `ORDER BY RANK`, and `RRF` fuses two scores for hybrid search. The score
ranks the rows and never appears in the result, the service not permitting it to be projected.

> **This needs a planner you build yourself, for now.** The validator resolves a function name against
> the operator table its `fun` property names, chained with the catalog reader — and the catalog reader
> resolves the *schema's own* functions. So a connection can reach these in principle; they are simply
> offered as a `SqlOperatorTable` rather than registered on the schema, which is what a connection
> would look them up through. Everything else on this page works through the provider.

## Documentation

- [Adapter README](src/Apache.Calcite.Cosmos.Adapter/README.md) — the package's own overview
- [DESIGN.md](src/Apache.Calcite.Cosmos.Adapter/DESIGN.md) — why Cosmos SQL is generated by hand, what
  the service was measured to do, and which assumptions are still unsettled
- [Cosmos DB SQL query reference](https://learn.microsoft.com/azure/cosmos-db/nosql/query/getting-started)
- [Apache Calcite for .NET](https://github.com/ikvmnet/calcite-dotnet) — the provider and calling conventions

## Building

```sh
dotnet build Apache.Calcite.Cosmos.slnx
```

The test suite runs against the Cosmos DB emulator, and reports inconclusive without one:

```sh
docker run -d --name cosmos-emu -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview
```

The emulator is not a substitute for the service — it has been found both to accept statements Azure
rejects and to reject features Azure implements, full text search among them. Set
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` to run the same suite against a real account.

## License

Apache License 2.0.
