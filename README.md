# Apache Calcite Cosmos Adapter

An [Apache Calcite](https://calcite.apache.org/) adapter that exposes [Azure Cosmos DB](https://learn.microsoft.com/azure/cosmos-db/) containers as relational schemas and pushes work down to Cosmos by generating **Cosmos SQL**.

Calcite runs in-process via [IKVM](https://github.com/ikvmnet/ikvm). The adapter does not use ADO.NET or JDBC — it plans a query into the subset of SQL that the Cosmos DB query engine supports and issues it directly.

## Packages

### `Apache.Calcite.Cosmos.Adapter` · `src/Apache.Calcite.Cosmos.Adapter`

The adapter itself. Registers a Cosmos calling convention with the Calcite planner so that filters, projections, joins, aggregations, and sorts are translated to Cosmos SQL wherever the Cosmos query engine can execute them. Anything that cannot be pushed down falls back to Calcite's enumerable runtime.

```sh
dotnet add package Apache.Calcite.Cosmos.Adapter
```

## Test and distribution projects

| Project | Purpose |
|---------|---------|
| `Apache.Calcite.Cosmos.Adapter.Tests` | Adapter tests |
| `dist-nuget` | Packages NuGet artifacts |
| `dist-tests` | Packages test artifacts for CI |

## Building

```bash
dotnet build Apache.Calcite.Cosmos.slnx
```

To produce the distribution layout under `dist/` exactly as CI does:

```bash
dotnet msbuild /p:Configuration=Release Apache.Calcite.Cosmos.dist.msbuildproj
```

Versioning is driven by [GitVersion](https://gitversion.net/) (see `GitVersion.yml`); local builds default to `1.0.0-dev`.

## Further reading

- [Apache Calcite documentation](https://calcite.apache.org/docs/)
- [Calcite adapters](https://calcite.apache.org/docs/adapter.html)
- [Cosmos DB SQL query reference](https://learn.microsoft.com/azure/cosmos-db/nosql/query/getting-started)
- [IKVM](https://github.com/ikvmnet/ikvm)

## License

Apache License 2.0.
