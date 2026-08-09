# Apache.Calcite.Cosmos.Adapter — Design

`Apache.Calcite.Cosmos.Adapter` exposes Azure Cosmos DB containers to Apache Calcite as
relational schemas, and pushes as much of the relational plan as possible down to Cosmos by
generating **Cosmos SQL**. Calcite's planner runs in-process via IKVM.

This document records the shape of the target language, the resulting design decision, and the
structure that follows from it.

> **Status.** Under development. Statement generation, container metadata, the schema and table
> layer, the scan/filter/project/sort/unnest/aggregate nodes with their conversion rules, and the
> converter that hands results to `ClrAsyncEnumerableConvention` are in place and tested. Items
> marked ✔ below exist; the rest are specification.
>
> One claim still rests on documentation rather than observation and needs a real Cosmos account
> to settle: that a multi-key `ORDER BY` requires a matching composite index. See *Verified
> against the emulator* and *Unvalidated assumptions*.

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

### Verified against the emulator

The following were established empirically against the Cosmos DB emulator
(`mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`) rather than taken from
documentation.

> **The emulator is not the service, and the difference has bitten twice.** It accepts statements
> Azure rejects, and rejects features Azure implements. Point `COSMOS_TEST_ENDPOINT` and
> `COSMOS_TEST_KEY` at a real account to run the same suite against one.
>
> | | emulator | Azure |
> | --- | --- | --- |
> | `ORDER BY t0` over `JOIN t0 IN c.tags` | accepted | **400** |
> | `FULLTEXTCONTAINS` and `ORDER BY RANK` | **400** | accepted |
>
> The first is why `CosmosSort` refuses any sort key rooted at an unnest alias: a single-key
> allowance stood for a long time on the emulator's word, and emitted a statement Azure will not
> run. `ORDER BY t0.x` is rejected too, so it is the alias and not the arity.

**Out-of-domain arithmetic fails the whole query.** `ASIN(2)`, `ACOS(2)`, `SQRT(-1)` and `LOG(0)`
each return a 400 rather than yielding undefined for the offending row. Calcite evaluates all four
as NaN, so pushing any of them down trades a row of NaN for a failed statement — over data no schema
lets the adapter check first. They are pushed anyway, `SQRT` and `LOG` always having been, and this
is recorded so that the consistency is a decision rather than an oversight.

**Ordering is a total order over JSON types.** Ascending:

```
undefined  <  null  <  boolean  <  number  <  string  <  array  <  object
```

`DESC` returns the exact reverse, including the placement of `undefined` and `null`. There is no
separate null-placement control.

This has a sharp consequence. Cosmos sorts nulls **first ascending and last descending**;
Calcite's `RelFieldCollation` defaults are the opposite on **both** counts — ascending defaults
to `NullDirection.LAST`, descending to `FIRST`. A sort on a nullable key therefore cannot
normally be pushed down, because doing so would return rows in an order the plan did not ask
for. `CosmosSort` refuses unless the placement matches or the key is non-nullable.

In practice this is what keeps sorting on `id` and the system properties available while
declining sorts on arbitrary document paths. The principled fix is to declare the collation the
adapter actually provides as a trait and let the planner insert a corrective sort, which is not
yet implemented.

**`IS_DEFINED` and `IS_NULL` are independent**, confirming the translation of SQL `IS NULL`:

| document | `IS_DEFINED(v)` | `IS_NULL(v)` |
| --- | --- | --- |
| `{"v": 1}` | true | false |
| `{"v": null}` | true | true |
| `{}` | false | false |

`WHERE v = null` matches only the explicitly-null document — it is *not* SQL `IS NULL`. The
emitted `(NOT IS_DEFINED(v) OR IS_NULL(v))` matches both that and the absent case, as intended.
Documents missing the sort property are returned by `ORDER BY`, not dropped.

**Aggregates do not share SQL's null handling.** Measured over `{10, 20, null, 5}` with two
documents lacking the property:

| Expression | Cosmos | SQL |
| --- | --- | --- |
| `COUNT(1)` | 6 | 6 |
| `COUNT(c.v)` | 4 — counts the JSON `null` | 3 |
| `SUM(c.v)` | `undefined` | 35 |
| `AVG(c.v)` | `undefined` | 11.67 |
| `GROUP BY c.g` where `g` is absent | group whose key is omitted from the result | a null group |

So `COUNT(*)` is safe, while the value aggregates agree with SQL only over an input that cannot
be null — the same reasoning that governs sort null placement. `CosmosAggregate` pushes down
accordingly and declines otherwise.

**A flat select list and an object constructor are not interchangeable.** The documentation
presents `SELECT e1 AS p1, e2 AS p2` as sugar for `SELECT VALUE { p1: e1, p2: e2 }`, and for
ordinary projections they behave identically. But the service rejects an aggregate inside an
object constructor:

```
Compositions of aggregates and other expressions are not allowed.
```

So a grouped projection has to be written flat. `CosmosQueryBuilder.FlatProjection` selects the
form and `CosmosAggregate` sets it. Nothing in the documentation indicates this; it surfaced only
by executing generated statements against a live service.

This is also why only `id`, `_ts` and `_etag` are declared non-nullable. A partition key path is
declared but a document may still omit it, and typing such a column non-nullable licences the
planner to rewrite `COUNT(x)` into `COUNT(*)` on a guarantee the data does not provide.

**The emulator does not implement composite indexes at all.** A container created with one
composite index reports zero on both the create response and a subsequent read, while excluded
paths in the same policy survive — so the definition is silently discarded rather than rejected.
Consistently, multi-key `ORDER BY` was accepted on containers with no composite index,
cross-partition, with mixed directions, and even on a path explicitly excluded from the index.

This contradicts the documented service behaviour. The composite index guard is retained on the
strength of the documentation; **the emulator can verify neither the guard nor the metadata
round-trip**, and both should be re-checked against a real account before the adapter is relied
on.

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

`Fields` binds an input field ordinal to a document path, and an entry may be **null** — the field is
a computed projection, which addresses nothing Cosmos can name. That is per ordinal rather than per
binding, and the distinction is worth stating: a projection of `UPPER(c.id)` alongside `c.id` leaves
the second still sortable, where clearing the whole binding declined every operator above it. An
operator refuses only when it actually reads an unbound ordinal.

> A rule may still fire where implementation then refuses. `CosmosSortRule` derives its bindings by
> name from the sort's input row type, so above a projection it names paths that do not exist, finds
> them resolvable, and converts; the refusal happens in `Implement`. That contradicts the rule
> contract below and is a known defect, not a design decision.

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
| `CosmosTableScan` | — | ✔ Terminal. One per container; nothing composes beneath it. |
| `CosmosFilter` | `CosmosFilterRule` | ✔ Only when every `RexNode` is translatable. Refused above a projection, since `WHERE` precedes `SELECT`. |
| `CosmosProject` | `CosmosProjectRule` | ✔ Renders as an object constructor. Rebinds field ordinals to the projected paths, or clears them when any projection is computed. |
| `CosmosSort` | `CosmosSortRule` | ✔ Carries `OFFSET`/`LIMIT`. Blocked if aggregation present. Multi-key sorts require a matching composite index; null placement must be honourable. |
| `CosmosUnnest` | `CosmosUnnestRule` | ✔ From `Correlate` over `Uncollect`, **never** from `Join`. |
| `CosmosAggregate` | `CosmosAggregateRule` | ✔ `COUNT(*)` always; `SUM`/`MIN`/`MAX`/`AVG` only over a non-nullable input. Blocked if a sort is present. Supersedes a path-only pruning projection. |

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

### Plan order is not clause order

A statement has one of each clause, and Cosmos evaluates them in a fixed order. An operator the
plan places *above* another may therefore be written into a clause that runs *before* it, which
silently changes the result rather than producing an error. Every node guards against the cases
that matter:

| Node | Refuses above | Because |
| --- | --- | --- |
| `CosmosFilter` | a projection | `WHERE` is evaluated against the source document, before `SELECT` |
| `CosmosFilter` | a row limit | `WHERE` runs before `OFFSET`/`LIMIT`, so it would filter the whole set and then restrict |
| `CosmosAggregate` | a row limit | `GROUP BY` runs before the restriction |
| `CosmosUnnest` | a sort, grouping, or row limit | a traversal multiplies rows, so it must precede all three |
| `CosmosSort` | another sort, or a grouping | one `ORDER BY` per statement; Cosmos rejects it alongside `GROUP BY` |

A sort *without* a restriction commutes with a filter, so that pairing stays available.

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

The scalar functions carried across are the ones where Calcite's standard operator table and Cosmos
agree on name, arity *and* meaning. Most are a direct rename; four are not, and those are the ones
worth naming:

| SQL | Cosmos | why it differs |
| --- | --- | --- |
| `ATAN2` | `ATN2` | Cosmos follows T-SQL's spelling |
| `TRUNCATE` | `TRUNC` | one argument only; the decimal-places form is unverified and declined |
| `CARDINALITY` | `ARRAY_LENGTH` | SQL counts a collection *or a map*, Cosmos only an array, so the map case is declined rather than answered wrongly — and `_MAP` is a map |
| `x MEMBER OF a` | `ARRAY_CONTAINS(a, x)` | the operands swap |
| `TRIM`/`LTRIM`/`RTRIM` | same | Calcite carries `[flag, chars, string]`; the flag picks the function, and only trimming spaces is translated |

`COALESCE` and `NULLIF` need no entry — the validator expands both to `CASE` before a `RexCall`
exists. Several plausible additions are deliberately absent: `LOG(x, base)` and `SQUARE` are not in
Calcite's standard table, so nothing can produce them; `CBRT` is, and Cosmos has no counterpart. The
`IS TRUE` / `IS FALSE` family and `IS DISTINCT FROM` are declined because reproducing their null
semantics over a property that may be *undefined* needs a Cosmos behaviour that has not been
measured, and a wrong answer is worse than a refused pushdown.

### Full text search

Cosmos has full text search and SQL does not, so there is nothing in Calcite's standard operator table
to map onto it. `CosmosOperators` defines the operators and `CosmosOperators.Instance` is the operator
table to chain into the one the validator is built with; without it a query cannot name these at all.

Signatures are the service's, from the query language reference:

| function | | |
| --- | --- | --- |
| `FULLTEXTCONTAINS(path, keyword)` | boolean | `WHERE` |
| `FULLTEXTCONTAINSALL(path, keyword, …)` | boolean | `WHERE` |
| `FULLTEXTCONTAINSANY(path, keyword, …)` | boolean | `WHERE` |
| `FULLTEXTSCORE(path, keyword, …)` | BM25 score | **`ORDER BY RANK` only** |
| `RRF(scoring function, …, weights)` | fused score | **`ORDER BY RANK` only** |

The first argument of every one is a **property path**, not an expression, and the translator holds
them to it: a call over anything that does not resolve to a path is declined rather than rendered.
Keywords bind as `@pN` like any other literal, so statement text stays independent of what is searched
for.

**The two scoring functions are a different kind of thing, and are not yet reachable.** The reference
is explicit that `FULLTEXTSCORE` and `RRF` may appear *only* in an `ORDER BY RANK` clause and **cannot
be part of a projection** — `SELECT FullTextScore(c.text, "kw") AS Score` is invalid. That is what
makes them hard here rather than tedious: Calcite sorts by field ordinal, so the natural plan for
`ORDER BY FULLTEXTSCORE(…)` is to project the score, sort on that column, and project it away — and
the first step is exactly what Cosmos forbids. Reaching `ORDER BY RANK` needs a rule that recognises
that shape and rewrites it into a rank clause without ever projecting the score.

What exists is the language layer below that rule. `CosmosQueryBuilder.RankBy` emits
`ORDER BY RANK <function>`, and refuses to combine it with an ordinary `ORDER BY` or with `GROUP BY` —
one `ORDER BY` clause per statement, and the reference says as much of `RRF` explicitly. The operator
table deliberately does **not** carry `FULLTEXTSCORE` or `RRF`: until the rule exists, a query that
could name one could only ever fail, and the translator refuses them with that reason where they
appear in a `WHERE` or a projection.

### Reading a value back

A row arrives as one JSON value and `CosmosJson` reads it into the representation Calcite holds that
SQL type in — **Java boxes, not CLR primitives**. A CLR `int` in a row compiles and then fails at the
first Calcite operator that casts it, a long way from where it was produced.

| JSON | as `ANY` / inside `MAP` | as a declared type |
| --- | --- | --- |
| string | `string` | `CHAR`, `VARCHAR` |
| number, whole | `java.lang.Long` | `TINYINT`…`BIGINT` as their boxes, `DECIMAL` from the raw digits |
| number, fractional | `java.lang.Double` | `REAL`, `FLOAT`, `DOUBLE` |
| `true` / `false` | `java.lang.Boolean` | `BOOLEAN` |
| object | `java.util.LinkedHashMap` | `MAP` |
| array | `java.util.ArrayList` | `ARRAY`, `MULTISET` |
| `null`, or absent | `null` | `null` |

Two choices worth stating. A whole number reads as a `Long` rather than a `Double` so that an
identifier or a count does not surface as `42.0`; the choice is the value's, there being no schema to
consult. And a document that disagrees with the declared type is **refused**, not coerced — reading
`42` as `VARCHAR` throws rather than yielding `"42"`, because a row type that bends to the data is a
suggestion rather than a declaration.

Nesting is not truncated at the map column's one-level type: `MAP<VARCHAR, ANY>` describes the top of
the document and the value goes as deep as the document does. Addressing is not limited either —
`ITEM` over `ANY` is `ANY`, so `_MAP['a']['b']['c']` keeps type-checking and the translator folds the
whole chain into one path, `c.a.b.c`, array indices included. What depth costs is planner metadata:
nothing below `_MAP` has a type, a key or a collation, so it can be addressed but not reasoned about.
The one limit is that a key must be constant — `_MAP[c.id]` names a property whose name is not known
until the row is read, and is declined.

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
    CosmosRules.cs                    ✔ Rule set for a convention instance
    CosmosSchema.cs                   ✔ Calcite Schema over a database
    CosmosTable.cs                    ✔ Calcite Table over a container; Statistic
    CosmosSchemaFactory.cs            ✔ SchemaFactory for JSON model registration
    Client/
      CosmosQueryExecutor.cs          ✔ Executes a rendered statement via the Cosmos SDK
      CosmosSequences.cs              ✔ The IAsyncEnumerable a compiled plan reads rows from
      CosmosJson.cs                   ✔ JSON value → the representation Calcite holds a value in
      CosmosSchemas.cs                ✔ Resolves the table's executor from the DataContext
      CosmosExecutionException.cs     ✔ The plan cannot reach what would execute it
      CosmosMaterializationException.cs ✔ A document does not hold what the query assumed
    Metadata/
      CosmosCompositeIndex.cs         ✔ Composite index and sort-key matching
      CosmosContainerMetadata.cs      ✔ Declared container facts; sort legality
      CosmosContainerMetadataReader.cs ✔ ContainerProperties → CosmosContainerMetadata
    Rel/
      CosmosRel.cs                    ✔ Implement contract
      CosmosTableScan.cs              ✔
      CosmosFilter.cs                 ✔
      CosmosProject.cs                ✔
      CosmosSort.cs                   ✔
      CosmosUnnest.cs                 ✔
      CosmosAggregate.cs              ✔
      Convert/                        ✔ One converter rule per node, and the one way out
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
convention or on the CLR conventions in `calcite-dotnet`, which is what let it be completed and
tested ahead of them, and is why it remains testable without one.

---

## Leaving the Convention

A subtree of Cosmos nodes is a statement, not rows. `CosmosToClrAsyncEnumerableConverter` is where
it becomes rows: it renders the statement, executes it, and reads the JSON value each row arrives
as into the row the plan above expects.

**The exit is asynchronous, and only asynchronous.** The v3 Cosmos SDK has no synchronous
data-plane API — a page arrives only by awaiting `FeedIterator.ReadNextAsync` — so a converter into
`ClrEnumerableConvention` or Calcite's `EnumerableConvention` could do nothing but wait on each
page, blocking a thread for a network round trip per continuation. That is the sync-over-async pull
`ClrAsyncEnumerableConvention` exists to keep out of a plan, and putting one at the leaf would
defeat it. The consequence is worth stating rather than discovering: **a query over a Cosmos table
plans only when the root is asked for in `ClrAsyncEnumerableConvention`.**

Three things follow from the row being one JSON value:

- **The result is always an object keyed by output field name.** A bare scan would otherwise render
  `SELECT VALUE c` and hand back the document itself, giving two row shapes for the materializer to
  tell apart. The converter projects the scan's own path bindings when nothing above it has
  projected, so `SELECT VALUE { … }` is the only shape that reaches the reader.
- **Fields are read by name, not position.** A Cosmos object constructor omits a property whose
  value is undefined, so the properties present in a row are a subset of the output fields.
- **A missing property and a null one are both SQL `NULL`.** Nothing in the row model can
  distinguish them, and SQL has no third value to distinguish them with.

A rendered statement carries one execution hint beyond its text. `OFFSET n LIMIT m` and `TOP n` bound
how many rows the statement can return, so `CosmosQuery.MaxItemCount` carries `n + m` (or `n`) and the
executor asks the service for pages that size. It is a page size, not a limit — it cannot change which
rows come back, only how many arrive per round trip — and without it a statement ending in `LIMIT 5`
fetches a full default page and pays for the rows it discards. An offset alone bounds nothing and asks
for nothing.

What executes the statement is *not* written into the plan. Calcite prepares a statement once and
executes it many times, so the plan holds the table's qualified name and `CosmosSchemas.GetExecutor`
walks it from the `DataContext`'s root schema on each run. A live `CosmosClient` compiled into the
expression tree would bind that plan to whichever schema instance happened to be current when it
was compiled. This is the same discipline as an adapter reaching its data source through
`Schemas.unwrap` over a convention's schema expression; it is spelled out here because the plan is a
`System.Linq.Expressions` tree and calls into managed code rather than carrying a linq4j expression.

A `CosmosTable` may hold no executor at all, which is what a table built from container metadata
alone is. Planning is unaffected — nothing about a statement or its cost depends on who runs it —
and enumerating such a plan is what fails, saying so. Most of the test suite plans against tables in
exactly that state.

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
- **SDK types stay at the edges.** `Microsoft.Azure.Cosmos` appears only in `Client/` and in the
  metadata reader. Planning, translation, and statement assembly are independent of the service,
  which is what lets the bulk of the suite run with no client, no emulator, and no network.
- **Parameterize rather than interpolate.** Literals that could carry user data bind as `@pN`.
- **Targeting.** The adapter targets .NET 8 (C# 12); tests target .NET 8 and .NET 10.

---

## Calcite's JDBC entry points under IKVM

`Frameworks.getPlanner` and `RelBuilder.create` open an internal Calcite JDBC connection. Under
IKVM that fails:

```
java.lang.RuntimeException: Error loading factory org.apache.calcite.jdbc.CalciteJdbc41Factory
 ---> java.lang.ClassNotFoundException: org.apache.calcite.jdbc.CalciteJdbc41Factory
```

The class is present and loadable — `Class.forName` on it from adapter code succeeds. The cause
is that **IKVM gives each assembly its own class loader**, where a JVM has one flat classpath.
Avatica's `UnregisteredDriver` resolves the factory with `Class.forName`, which binds against the
calling class's loader — `avatica.core`. The factory lives in `calcite.core`, and avatica does
not reference calcite; the dependency runs the other way. So the lookup fails, the driver's type
initializer throws, and every entry point that opens a connection fails with it.

The fix is to publish the assembly into the boot class loader, restoring the flat-classpath
assumption the Java code was written against:

```csharp
ikvm.runtime.Startup.addBootClassPathAssembly(typeof(org.apache.calcite.jdbc.CalciteFactory).Assembly);
```

This must run before the driver is first touched, since a type initializer runs once and caches
its failure. The test assembly does it from a `[ModuleInitializer]`; `[AssemblyInitialize]` is
not reliably early enough.

The adapter itself does not need any of this: it never opens a connection, and the SQL planning
in `CosmosSqlPlanningTests` drives `SqlParser`, `SqlValidator` and `SqlToRelConverter` directly,
none of which require the driver. The note is recorded because any consumer reaching for
`Frameworks` or `RelBuilder` will hit it.

---

## Cost

Two properties of a predicate dominate what a Cosmos query costs, and neither is visible in the
shape of the plan, so `CosmosFilter.computeSelfCost` reflects both:

- **Naming the partition key** confines execution to one physical partition rather than fanning
  out across every one and merging. `CosmosPartitionKeyExtractor` recovers the value from a
  conjunction of equalities against constants; a disjunction or a range predicate does not
  qualify, since either may span partitions. What it recovers also reaches the executor, so such
  a query becomes single-partition without the caller asking.
- **Filtering on an unindexed path** forces a scan of it. `CosmosContainerMetadata.IsPathIndexed`
  applies the documented precedence — deeper beats shallower, `/?` beats `/*` at equal depth —
  over the container's included and excluded paths. `id` and `_ts` are always indexed.

Index coverage bears on cost only. A predicate or sort over an unindexed path still runs; it is
the composite index requirement for multi-key sorts that affects legality.

---

## Unvalidated assumptions

Recorded so they are not mistaken for tested behaviour.

**The composite index requirement.** `CosmosContainerMetadata.IsSortSupported` refuses a
multi-key `ORDER BY` without a matching composite index. The rule is documented, but the
emulator implements composite indexes not at all, so nothing exercises it end to end. If the
real service is more permissive, the guard silently costs pushdown on every multi-key sort.
A real account can now settle this — see *Verified against the emulator* for how to point the suite
at one — and it is the first thing worth measuring there.

**Null placement on non-nullable keys.** Sorting a non-nullable key is accepted regardless of
requested placement, on the grounds that a key which cannot be null has no null ordering to
disagree about. This is sound provided the declared nullability is accurate — which for the map
row model means `id` and the system properties, whose presence the service guarantees.

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
