# Apache.Calcite.Cosmos.Adapter — Design

`Apache.Calcite.Cosmos.Adapter` exposes Azure Cosmos DB containers to Apache Calcite as
relational schemas, and pushes as much of the relational plan as possible down to Cosmos by
generating **Cosmos SQL**. Calcite's planner runs in-process via IKVM.

This document records the shape of the target language, the resulting design decision, and the
structure that follows from it.

> **Status.** Early development. `CosmosConvention` and `CosmosRel` exist. Everything described
> under *Planned structure* is not yet built. This document is the specification those pieces
> are being built against.

---

## Scope

- **Adapter, not a provider.** This package makes Cosmos DB queryable *from* Calcite. It is not
  an entry point for applications to execute SQL; that is the role of an ADO.NET provider.
- **No ADO.NET, no JDBC, no Avatica.** The adapter renders Cosmos SQL text and executes it
  through the Cosmos data-plane SDK. There is no intermediate relational protocol.
- **Cosmos DB for NoSQL only.** The MongoDB, Cassandra, Gremlin, and Table APIs are out of
  scope; they have their own query languages and, in the Cassandra and MongoDB cases, their own
  Calcite adapters upstream.

---

## The Target Language

Cosmos SQL is SQL-*shaped* but is not a relational language. Its surface is closed and small.
The design below follows directly from these properties, so they are recorded explicitly.

### Supported clauses

`SELECT`, `FROM`, `WHERE`, `GROUP BY`, `ORDER BY`, `ORDER BY RANK`, `OFFSET LIMIT`, and
subqueries. That is the complete list.

### Reserved keywords

`BETWEEN`, `DISTINCT`, `LIKE`, `IN`, `TOP`. That is the complete list.

There is no `UNION`, `INTERSECT`, or `EXCEPT`; no `HAVING`; no `CASE`/`WHEN`; no `WITH`/CTE; no
window functions; no `CAST`; and no DML. `SETUNION` is an array function, not a set operator
over rows.

### Four properties that determine the design

**1. `JOIN` has no join predicate.**

```
<from_specification> ::= <from_source> {[ JOIN <from_source>][,...n]}
<from_source>        ::= <container_expression> [[AS] input_alias] | input_alias IN <container_expression>
<container_expression> ::= ROOT | container_name | input_alias
                         | <container_expression> '.' property_name
                         | <container_expression> '[' "property_name" | array_index ']'
```

There is no `ON` production. Cosmos `JOIN` cross-products a document with its own nested arrays
— it is `UNNEST`/`CROSS APPLY` spelled `JOIN`. Relational joins are not expressible, and the
documented workaround for one is to inline a literal array of reference data into a subquery.

**2. There are no derived tables.** A Cosmos query always returns a single column, so only
*multi-value* and *scalar* subqueries exist. Subqueries in `FROM` appear exclusively as
`JOIN x IN (...)` and are **item-scoped** — they iterate an array belonging to the current
document. `FROM (SELECT ... FROM container WHERE ...) AS t` has no equivalent.

**3. The result is a JSON value stream, not a tuple stream.** Multi-column projection is
syntactic sugar for an object constructor:

```
SELECT <e1> AS p1, ..., <eN> AS pN     ≡     SELECT VALUE { p1: <e1>, ..., pN: <eN> }
```

**4. `GROUP BY` and `ORDER BY` cannot appear in the same query.** Additionally, neither
`GROUP BY` nor `DISTINCT` supports continuation tokens, so grouped and distinct results are not
resumable across pages.

### What a container declares

A container has **no row schema**. Two items in the same container may share nothing but `id`.
But a container is not metadata-free — it declares a good deal, and all of it is *planner*
metadata rather than *type* metadata:

| Declared / guaranteed | Source | Planner value |
| --- | --- | --- |
| `id` — required, string, unique within a logical partition | Service guarantee | Key component |
| Partition key path(s) — up to 3, hierarchical | Container definition | Distribution; filter priority |
| `_ts` (epoch seconds), `_etag`, `_rid`, `_self` | Service-generated on every item | Typed columns; `_ts` is a real timestamp |
| Included / excluded index paths | Indexing policy | Whether a predicate is cheap or a scan |
| Composite indexes (ordered, with direction) | Indexing policy | **Whether `ORDER BY` is legal at all** |
| Unique key policy | Container definition | Unique keys |
| Computed properties | Container definition | Named, queryable, declared paths |
| Tuple / spatial / full-text / vector indexes | Indexing policy | Function pushdown eligibility |

Two of these carry hard consequences:

- `id` and `_ts` are **always** indexed when the indexing mode is `Consistent`; `_etag` is
  excluded by default; the partition key is *not* indexed unless it is `/id`.
- *"Queries that have an `ORDER BY` clause with two or more properties require a composite
  index."* The index paths must match the `ORDER BY` sequence, and the directions must match
  exactly or be exactly inverted. A multi-property sort without a matching composite index is
  not a slow query — it is an invalid one.

The last point is the important one: **whether a `Sort` is pushable is a function of container
metadata, not of the plan.** `CosmosSortRule` must read the indexing policy.

---

## Decision: hand-built SQL, not `RelToSqlConverter`

Because Cosmos SQL is textually SQL-like, routing `RelNode` trees through Calcite's
`RelToSqlConverter` and a custom `SqlDialect` is the obvious first instinct. It is the wrong
choice, for two independent reasons.

### `SqlImplementor`'s core mechanism is unavailable

`SqlImplementor` has exactly one strategy for a plan that does not collapse into a single flat
`SELECT`: when the next operator would overwrite an already-occupied clause, it wraps the
current result in a **sub-select** and opens a fresh clause context. The `Result` type, the
`Clause` ordering, and the alias bookkeeping all exist to serve that mechanism.

Cosmos has no derived tables (property 2). The single most valuable thing `RelToSqlConverter`
would give us is the one thing the target cannot express — and the converter decides to nest
based on internal clause state, not on anything a `SqlDialect` can veto. When it nests, it
emits SQL Cosmos rejects.

### `SqlDialect` is the wrong lever

`SqlDialect` hooks *unparsing*: quoting, operator spelling, `OFFSET`/`FETCH` syntax. Every
Cosmos divergence is **structural**, not lexical:

| Divergence | Why a dialect cannot fix it |
| --- | --- |
| `JOIN` means unnest | Requires rewriting the plan, not the tokens |
| Projection is an object literal | `SELECT VALUE { … }` has no `SqlNode` |
| Identifiers are paths (`c.prop`) | Not a quoted `"c"."prop"` identifier pair |
| No `CASE` | Nothing valid to unparse `SqlCase` into |
| `GROUP BY` excludes `ORDER BY` | A planner-level constraint, not a syntax one |

### The counter-argument, and why it still loses

Because the pushable envelope is so small, a Filter+Project+Sort+Limit over one container is a
shallow tree that would never *trigger* nesting — so the derived-table problem might never bite
in practice. True, but it concedes the point: we would carry the full weight of
`RelToSqlConverter` for a job that never needs its hard part, while still fighting it on
`SELECT VALUE`, path identifiers, unnest, `CASE`, and `@p` parameter syntax.

### What is worth borrowing

The *shape* of `RexNode` → target-expression translation, not the implementation. See
*Expression translation* below.

---

## Planned Structure

### Convention

`CosmosConvention` is a `Convention.Impl` bound to a single container. It is not a singleton:
a query spanning two containers uses two instances, and the planner inserts converters between
them. `CostMultiplier` (0.8) biases the planner toward pushing work into Cosmos.

`register(RelOptPlanner)` will add the converter rules listed below.

### Implementor

`CosmosRel` nodes do not return SQL fragments. Each contributes state to a mutable
`CosmosImplementor`, which renders the final statement once the whole subtree has been visited:

```csharp
public interface CosmosRel : RelNode
{
    void Implement(CosmosImplementor implementor);
}
```

Naming follows the house style in the sibling `calcite-dotnet` repository: members overriding
Java declarations keep their lowercase Java names (`register`, `getInterface`), while new
.NET-side contracts are PascalCase.

`CosmosImplementor` accumulates:

| Field | Renders to |
| --- | --- |
| Root alias | `FROM <container> <alias>` |
| Unnest bindings | `JOIN x IN <path>` (ordered) |
| Projection | `SELECT VALUE { … }` or `SELECT VALUE <expr>` |
| Predicate | `WHERE …` |
| Group keys + aggregates | `GROUP BY …` |
| Collations | `ORDER BY …` |
| Offset / fetch | `OFFSET n LIMIT m` |
| Parameters | `@p0`, `@p1`, … plus a bound value list |

This mirrors how Calcite's own non-JDBC adapters work. **Cassandra is the closest precedent** —
CQL is likewise SQL-shaped, and Calcite still hand-builds it rather than routing through
`RelToSqlConverter`.

### Pushdown envelope

| Node | Rule | Notes |
| --- | --- | --- |
| `CosmosTableScan` | — | Terminal. One per container; nothing composes beneath it. |
| `CosmosFilter` | `CosmosFilterRule` | Only when every `RexNode` is translatable. |
| `CosmosProject` | `CosmosProjectRule` | Renders as an object constructor. |
| `CosmosSort` | `CosmosSortRule` | Carries `OFFSET`/`LIMIT`. Blocked if aggregation present. Multi-key sorts require a matching composite index. |
| `CosmosAggregate` | `CosmosAggregateRule` | `COUNT`, `SUM`, `MIN`, `MAX`, `AVG` only. Blocked if a sort is present. |
| `CosmosUnnest` | `CosmosUnnestRule` | From `Uncollect`/`Correlate`, **not** from `Join`. |

Deliberately absent, and not to be added later without revisiting this document:

- **No `CosmosJoin`.** Relational joins are inexpressible (property 1). No rule may convert a
  `Join`. Array traversal arrives via `Uncollect`/`Correlate` instead.
- **No `CosmosUnion` / `CosmosIntersect` / `CosmosMinus`.** No set operators exist. Calcite's
  enumerable runtime handles these in-process.
- **No `CosmosValues`.** There is no container-independent row source.

`CosmosAggregate` and `CosmosSort` are **mutually exclusive** (property 4). This is enforced as
a rule guard: each rule must refuse to fire if the implementor state already holds the other.
No `SqlDialect` could express this constraint, which is itself evidence for the chosen design.

Anything that cannot be pushed down falls back to Calcite's enumerable runtime. The adapter
must **never** emit a statement it is unsure of — an untranslatable operator is a signal to
decline conversion, not to guess.

### Expression translation

A `RexVisitor` emits Cosmos scalar expressions directly, with no `SqlNode` round-trip. It is
roughly the effort of populating a dialect's operator table, minus the intermediate tree, and
it provides the natural place to **reject** unsupported operators so the planner falls back
cleanly.

Specific obligations:

- `RexInputRef` → a path expression (`c.prop`, `c.a.b`, `c["odd name"]`), not a quoted
  identifier. Bracket form whenever the property name is not a bare identifier.
- `RexLiteral` → JSON literal, or a bound `@pN` parameter for anything non-trivial.
- `CASE` → nested ternary (`? :`) where the arms permit it; otherwise decline.
- Unknown operator → decline. Never emit a best guess.

### The row model

Calcite has **no JSON type**. `SqlTypeName` in 1.41.0 has no `JSON` constant, and Calcite's
SQL/JSON functions (`JSON_VALUE`, `JSON_QUERY`, `JSON_EXISTS`, …) follow SQL:2016, where JSON is
*character data* — VARCHAR in, VARCHAR out, re-parsed per call. That is the wrong substrate.

What Calcite does have:

| Option | Availability | Assessment |
| --- | --- | --- |
| `MAP<VARCHAR, ANY>` + `ITEM` | Since forever | **Chosen.** The pattern the MongoDB and Elasticsearch adapters use for a `_MAP` column. |
| `VARIANT` | 1.41.0 (`SqlTypeName.VARIANT`, `org.apache.calcite.runtime.variant`, operators `VARIANT`/`VARIANTNULL`/`TYPEOF`) | Semantically the best fit — `item`, `cast`, `getTypeString`. No shipped adapter models a row type on it; planner pushdown through VARIANT is unproven. Revisit. |
| `DynamicRecordType` + `DYNAMIC_STAR` | Present in 1.41.0 | Nicer ergonomics (`c.name` rather than `ITEM(_MAP,'name')`), but nested paths fall back to field access on an `ANY` anyway. Worth evaluating as a surface layer, not as the substrate. |

#### Base: one map column

Every Cosmos query returns exactly one JSON value per row, so the base row type is **one
column**, not N. This is not a compromise — the two models agree exactly:

| Cosmos | Calcite |
| --- | --- |
| `SELECT VALUE c` | the single `_MAP` column |
| `SELECT a, b` → `{a:…, b:…}` | a map, which is what the column already is |
| `c.address.city` | `ITEM(ITEM(_MAP,'address'),'city')` |
| `c["odd name"]` | `ITEM(_MAP, 'odd name')` |

`ITEM` → path expression is close to 1:1, and the coercion of a flat select list into an object
stops being an impedance mismatch — it is the identity of the column.

#### Promoted columns

A single column has one field ordinal, and Calcite's planner metadata is ordinal-based:
`Statistic` exposes `getKeys`, `getCollations`, `getDistribution`, `getReferentialConstraints`,
and `getRowCount`, with keys and collations expressed over field ordinals. With only `_MAP`,
none of it is expressible — which is why the Mongo and Elasticsearch adapters supply no
statistics at all.

The container metadata table above is exactly the material those methods want, so the row type
is `_MAP` **plus promoted scalar columns** for paths that are declared or service-guaranteed:

| Promoted column | Type | Enables |
| --- | --- | --- |
| `id` | `VARCHAR NOT NULL` | `getKeys` (with partition key) |
| `_ts` | `BIGINT` | A genuinely typed timestamp |
| `_etag` | `VARCHAR` | Optimistic concurrency |
| Partition key path(s) | declared | `getDistribution`; single-partition detection |
| Composite index paths | declared | `getCollations`; `CosmosSortRule` legality |
| Computed properties | declared | Named projections |

**Only declared or guaranteed paths may be promoted. Never a sampled one.** Sampling a
container to guess its shape is fine as an opt-in convenience for projection ergonomics, but it
must never feed `Statistic` — an inferred key or collation that is wrong produces a silently
incorrect plan, not a slow one.

#### Residual type problems

- **No date/time type.** Cosmos JSON has six types — `undefined`, `null`, boolean, number,
  string, array, object. Dates are ISO 8601 strings or epoch numbers by application convention,
  and nothing declares which. `_ts` is the sole exception (epoch seconds, service-defined).
  Temporal predicates on user paths are only pushable once the encoding is declared in the
  model; otherwise decline.
- **`undefined` ≠ `null`.** A missing property and a null-valued property are distinct in
  Cosmos. In the map model this is representable — the key is absent versus present-and-null —
  which is strictly better than collapsing both to SQL `NULL`. Predicates distinguishing them
  translate to `IS_DEFINED`. Promoted columns *do* lose the distinction; that is the price of
  promotion and applies only to paths whose presence is guaranteed anyway.
- **Heterogeneous types per path.** The same path may be a string in one item and a number in
  the next. `ANY` absorbs this; a promoted column does not, which is a second reason promotion
  is restricted to declared paths.

---

## Planned Project Layout

```
src/
  Apache.Calcite.Cosmos.Adapter/
    CosmosConvention.cs               ✔ Per-container calling convention
    CosmosImplementor.cs              ✔ Mutable SQL accumulator
    CosmosRules.cs                    Rule set for a convention instance
    CosmosSchema.cs                   Calcite Schema over a database
    CosmosSchemaFactory.cs            SchemaFactory for JSON model registration
    CosmosTable.cs                    Calcite Table over a container
    Metadata/
      CosmosCompositeIndex.cs         ✔ Composite index and sort-key matching
      CosmosContainerMetadata.cs      ✔ Declared container facts; sort legality
    Rel/
      CosmosRel.cs                    ✔ Implement contract
      CosmosTableScan.cs
      CosmosFilter.cs
      CosmosProject.cs
      CosmosSort.cs
      CosmosAggregate.cs
      CosmosUnnest.cs
      Convert/                        One converter rule per node
    Sql/
      CosmosSql.cs                    ✔ Lexical primitives: identifiers, paths, JSON literals
      CosmosPath.cs                   ✔ Immutable property path rooted at a FROM alias
      CosmosParameterList.cs          ✔ @pN binding
      CosmosQueryBuilder.cs           ✔ Statement assembly and language-constraint enforcement
      CosmosRexTranslator.cs          ✔ RexNode → Cosmos scalar expression
      CosmosTranslationException.cs   ✔ Refusal signal
    Internal/
      BigDecimalConverter.cs          ✔ Lossless BigDecimal → decimal
  Apache.Calcite.Cosmos.Adapter.Tests/
```

✔ marks what exists today. The `Sql/` layer is deliberately free of any dependency on the
convention or on the CLR enumerable conventions being built in `calcite-dotnet`, so it can be
completed and tested ahead of them.

---

## Design Constraints

- **Generate only what Cosmos accepts.** Declining to push down is always correct; emitting a
  statement the service rejects is not. Every rule and every expression translation must have a
  refusal path.
- **Planner metadata comes only from declared facts.** `Statistic` may be populated from the
  container definition and indexing policy, never from sampled documents. A wrong key or
  collation yields an incorrect plan, not a slow one.
- **Rule legality can depend on container metadata.** `CosmosSortRule` consults the indexing
  policy. This is expected, not a leak.
- **No relational joins.** Not now, not behind a flag. The grammar has no join predicate.
- **One container per convention instance.** Cross-container work happens above the convention
  boundary, in Calcite.
- **No ADO.NET or JDBC dependency.** Execution goes through the Cosmos SDK.
- **Parameterize rather than interpolate.** Literals that could carry user data bind as `@pN`.
- **Targeting.** The adapter targets .NET 8 (C# 12); tests target .NET 8 and .NET 10.

---

## References

- [Query language overview](https://learn.microsoft.com/en-us/cosmos-db/query/overview)
- [Clauses](https://learn.microsoft.com/en-us/cosmos-db/query/clauses) ·
  [Keywords](https://learn.microsoft.com/en-us/cosmos-db/query/keywords)
- [FROM](https://learn.microsoft.com/en-us/cosmos-db/query/from) ·
  [SELECT](https://learn.microsoft.com/en-us/cosmos-db/query/select) ·
  [GROUP BY](https://learn.microsoft.com/en-us/cosmos-db/query/group-by) ·
  [ORDER BY](https://learn.microsoft.com/en-us/cosmos-db/query/order-by)
- [Subqueries](https://learn.microsoft.com/en-us/cosmos-db/query/subquery) ·
  [Pagination](https://learn.microsoft.com/en-us/cosmos-db/query/pagination)
- [Indexing policies](https://learn.microsoft.com/en-us/cosmos-db/indexing-policies) —
  composite index requirements, default indexing of `id`/`_ts`
- [Databases, containers, and items](https://learn.microsoft.com/en-us/azure/cosmos-db/resource-model) ·
  [Partitioning](https://learn.microsoft.com/en-us/azure/cosmos-db/partitioning-overview) ·
  [Unique keys](https://learn.microsoft.com/en-us/azure/cosmos-db/unique-keys)
- [Calcite adapters overview](https://calcite.apache.org/docs/adapter.html)
