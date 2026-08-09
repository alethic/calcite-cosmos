# Apache.Calcite.Cosmos.Adapter — Design

`Apache.Calcite.Cosmos.Adapter` exposes Azure Cosmos DB containers to Apache Calcite as
relational schemas, and pushes as much of the relational plan as possible down to Cosmos by
generating **Cosmos SQL**. Calcite's planner runs in-process via IKVM.

This document records the shape of the target language, the resulting design decision, and the
structure that follows from it.

> **Status.** Under development. Statement generation, container metadata, the schema and table
> layer, the scan/filter/project/sort/unnest/aggregate/rank nodes with their conversion rules, and
> the converter that hands results to `ClrAsyncEnumerableConvention` are in place and tested. Items
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
> | multi-key `ORDER BY`, no composite index | accepted | **400** |
>
> The first is why `CosmosSort` refuses any sort key rooted at an unnest alias: a single-key
> allowance stood for a long time on the emulator's word, and emitted a statement Azure will not
> run. `ORDER BY t0.x` is rejected too, so it is the alias and not the arity.

**A hundred-term `IN` is served by the index.** Measured on a real account with
`PopulateIndexMetrics`: `WHERE c.category IN (@k0, ..., @k99)` reports
`{"UtilizedIndexes":{"SingleIndexes":[{"IndexSpec":"/category/?"}]},"PotentialIndexes":{"SingleIndexes":[]}}`
— the index on the restricted path is used and nothing is left unused. This is the form the lookup
join emits, at the batch size it emits, and the measurement is what says the feature is an
improvement rather than a scan with a large predicate bolted on.

Also measured: the index metrics header carries **JSON**, not the prose the documentation shows. The
prose is what the portal renders.

**A multi-key `ORDER BY` really does need a composite index spanning its keys.** Measured on a real
account against a container built with the default indexing policy and so with no composite index at
all: `ORDER BY c.category, c.price` is rejected with **400**, while `ORDER BY c.price` over the same
container is served. So `CosmosContainerMetadata.IsSortSupported` is refusing exactly what the
service refuses, and the guard costs no pushdown that was ever available.

This is the third case where the emulator's answer was the wrong one, and it is why it could not be
settled until now: the emulator discards composite indexes on create, reporting none on the create
response and none on a subsequent read, and then accepts the multi-key form regardless.

**The document count lags, and by more than a moment.** A container read reports `documentsCount` in
its `x-ms-resource-usage` header, and immediately after writing four documents it reports **zero** —
the statistic is computed in the background. That is what the row count in `getStatistic` is, and it
is what a planner row count is allowed to be: approximate and stale. It is not what the rule against
inferring from documents forbids, which is about the *shape* of the data — a wrong shape yields an
incorrect plan, a wrong row count yields a slow one.

The partition count is answered immediately, being a fact about the container rather than its
contents.

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

**A rule decides on the same binding implementation will use.** `CosmosImplementor.TryBindOutput`
derives it by walking the input and mirroring each node's `Implement`; a node it does not know returns
nothing and the rule declines. Deriving it from the input row type instead — which every rule used to
do — reads a projection's aliases as document properties: `CosmosSortRule` would name `c.u` for a
column called `u`, find it resolvable, convert, and leave the refusal to `Implement`, contrary to the
rule contract below. Worse, it checked a multi-key sort against the container's composite indexes
using those invented paths, so the legality answer was about paths the container does not have.

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

**The two scoring functions are a different kind of thing.** The reference is explicit that
`FULLTEXTSCORE` and `RRF` may appear *only* in an `ORDER BY RANK` clause and **cannot be part of a
projection** — `SELECT FullTextScore(c.text, "kw") AS Score` is invalid. That is what makes them
structural rather than tedious. Calcite sorts by field ordinal, so ordering by an expression outside
the select list becomes three nodes:

```
LogicalProject(id=[$0])                                     drops the score
  LogicalSort(sort0=[$1])                                   sorts on it
    LogicalProject(id=[$1], $f1=[FULLTEXTSCORE($0, 'kw')])  adds it
```

and the innermost is a statement Cosmos will not run. `CosmosRankRule` matches the whole shape and
collapses it into one `CosmosRank`, which projects what survives and renders the score into the clause
and nowhere else.

**Matching all three is what makes it safe.** Seeing only the sort would leave the score in the row
type for something above to read, and it would read null — the statement never projects it. Requiring
the outer projection to discard it is how the rule knows nothing does. A consumer reaches that shape
through `RelRoot.project()`, which is where the extra column stops being output; a plan taken from
`RelRoot.rel` still carries it, and is then correctly refused rather than silently returning nulls.

`CosmosQueryBuilder.RankBy` emits the clause and refuses to combine it with an ordinary `ORDER BY` or
with `GROUP BY` — one `ORDER BY` per statement, and the reference says as much of `RRF` explicitly.
The scoring functions are in the operator table so a query can name them, and the translator permits
them through `TranslateRank` alone; everywhere else is a place the service rejects them, so a `WHERE`
or a select list containing one declines.

### What a table tells the planner

`getStatistic` reports **keys** derived from declared facts, and a **row count** where the service was
asked for one. It reports **no collations**, and that absence is the point.

A statistic's collations are the order a scan's rows *already arrive in* — `RelOptTableImpl`
hands them to `RelMdCollation` as the collation of the scan. Reporting a composite index there claims
a Cosmos scan comes back sorted, which licences the planner to drop a `Sort` asking for exactly that
order. It does not: Cosmos guarantees no order without an `ORDER BY`, whatever is indexed. This was
reported for a while, and a probe of `mq.collations(scan)` showed the planner being told a bare scan
was ordered by `(id, _ts)`.

What a composite index decides is whether a multi-key `ORDER BY` is **legal**, and that question
belongs to the rule that pushes the sort, where `CosmosContainerMetadata.IsSortSupported` answers it.
Calcite's own adapters agree: Cassandra keeps its clustering order on the table for its rules to read
and does not implement `getStatistic` at all — and a Cassandra clustering order genuinely *is* the
storage order, which a Cosmos composite index is not.

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
    CosmosColumnStrategies.cs         ✔ Which columns an INSERT may omit, and which it may not name
    Client/
      CosmosQueryExecutor.cs          ✔ Executes a rendered statement via the Cosmos SDK; writes items
      CosmosSequences.cs              ✔ The IAsyncEnumerable a compiled plan reads rows from, and writes through
      CosmosJson.cs                   ✔ JSON value → the representation Calcite holds a value in
      CosmosDocument.cs               ✔ The reverse: a row → the JSON document it describes
      CosmosWrite.cs                  ✔ What a write does, decided while the plan is built
      ICosmosItemWriter.cs            ✔ Creating and deleting documents
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
      CosmosRank.cs                   ✔ ORDER BY RANK, which subsumes the projection
      CosmosLookupJoin.cs             ✔ Fetches only the documents another side's keys could match
      CosmosTableModify.cs            ✔ INSERT and DELETE; not in the convention, having no statement
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

### Not every statement is a query

A lookup by `id` and a complete partition key is a **point read**: `ReadItem`, about 1 RU, no query
engine, against the 2.3 RU a query costs at best. `CosmosQuery.PointReadId` carries the `id` when the
statement is one, and the executor reads instead of querying.

**A point read applies no predicate**, and that governs when it is offered. Under
`WHERE id = 'x' AND pk = 'y' AND price > 100` a read would return a document the query excludes — a
wrong answer, not a slow one. So every top-level conjunct must be one of the equalities pinning `id`
or a partition key path, and all of them must be pinned. `CosmosPartitionKeyExtractor` answers that
question in the direction its name does not suggest: `Collect` records what a predicate pins and
ignores the rest, and `CoversExactly` asks whether there *is* a rest.

The rest of the statement rules it out just as firmly. A read returns one document, whole, so an
ordering, a row limit, a grouping and an array traversal each describe something a document is not.
`CosmosImplementor.Build` withholds the read for any of them.

**The projection is the interesting one.** A read returns the document; the statement would have
returned `SELECT VALUE { … }`. Two row shapes again — and this time the answer is not to force one,
because forcing the projected shape is exactly the query the read is avoiding. Instead the converter
builds a second row builder that walks each output field's *path* in the returned document, `_MAP`
being the empty path. It can only do that where every output field addresses a path, so a computed
projection withdraws the read and the statement executes as the query it already is.

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

## Writing

**Cosmos SQL has no DML, and that is not a reason the adapter cannot write.** The query language has
no `INSERT`, `UPDATE` or `DELETE`, but the SDK has item CRUD, and Calcite expresses a write as a
`TableModify` node consuming rows rather than as generated SQL. So the write path shares nothing with
the read path below the plan: no implementor, no statement, no `CosmosQuery`. Nothing here renders
text.

Which settles where the node lives. A subtree in `CosmosConvention` *is* a statement, and a write is
not one, so `CosmosTableModify` is in `ClrAsyncEnumerableConvention` — a node whose input is rows and
whose effect is a sequence of SDK calls. That makes it the same shape as `CosmosLookupJoin`: a node
that knows about a container without being inside the convention that renders one.

Two consequences worth stating because neither is obvious.

**Neither Clr convention has a modify node**, so this is the first. Calcite's own
`EnumerableTableModify` is not a model to copy: it writes through
`ModifiableTable.getModifiableCollection()`, calling `Collection.add` and `Collection.remove` on
whatever the table hands back. For Cosmos that collection would have to block on `CreateItemAsync`
per element, which is the sync-over-async pull the asynchronous convention exists to keep out of a
plan — at the leaf, where it is worst.

**`ModifiableTable` is therefore not implemented, and is not needed.** Measured:
`SqlToRelConverter.createModify` falls back to `LogicalTableModify.create` when the target unwraps to
no `ModifiableTable`, and `DELETE` and `UPDATE` plan through that fallback unchanged. A rule matching
`LogicalTableModify` over a Cosmos table is the whole entry point.

### What an insert writes

The row model makes this the real question. A document *is* the map column, and the promoted columns
are paths within the same document, so an insert naming columns is describing one document twice over.

**The document is `_MAP`. A promoted column sets the property it projects.** Where both are supplied
the promoted column wins, which is the projection that produced it run backwards —
`{…map, id: …}`. Refusing the overlap instead was considered and rejected: it would refuse
`INSERT INTO t (_MAP, "id") SELECT doc, key FROM …`, which is the natural way to write a document
whose id comes from somewhere else.

**Promoted columns alone cannot describe a useful document, and that is why the map column is
primary.** The promoted set is `id`, `_ts`, `_etag` and the partition key paths — nothing else is
declared, so nothing else can be promoted. An insert restricted to them could only ever write
`{id, category}`. Every real document has properties that are not promoted columns, and the map
column is the only route to them.

**A promoted column contributes its property only when the value is not null.** This is forced. An
unmentioned column arrives as SQL `NULL`, so without this rule `INSERT INTO t (_MAP) VALUES (m)` would
write `{…m, id: null, category: null}` and destroy the document it was given. The cost is that a
promoted column cannot write a JSON null; the map column can, because a map distinguishes an absent
key from a present one holding null. That is *undefined ≠ null* from the row model, resolved in the
only direction that leaves the primary route working.

**`_ts` and `_etag` cannot be written at all.** They are service-maintained, so a supplied value would
be ignored. They are declared `ColumnStrategy.STORED`, and the validator then refuses to let one be
named — `Cannot INSERT into generated column '_ts'` — rather than the adapter discovering it later or
dropping it without comment.

> **`STORED`, not `VIRTUAL`, and the difference is not cosmetic.** Both refuse a write. `VIRTUAL`
> additionally means *not stored*: measured, it makes `RelOptTableImpl.toRel` drop the column from the
> scan and project a literal null in its place, so `SELECT _ts` returns nothing the service holds.
> Forty tests failed on it at once. The row type is identical either way, which is why the regression
> guard asserts the *plan* rather than the type.

**Nothing invents an `id`.** A document reaching the service without one is the service's business to
accept or refuse, and guessing here would make the adapter the author of a key the caller did not
choose.

**The service's own properties are stripped from the document, wherever in it they appear.** `_ts`
cannot be *named*, but it arrives inside the map column whenever one document is copied to another —
which is what `INSERT INTO t (_MAP) SELECT "_MAP" FROM t2` hands over, and the obvious use of that
statement. **Measured, and this is a decision rather than a requirement:** with the stripping removed
a document carrying a bogus `_ts`, `_etag` and `_rid` was still accepted, and still came back with
values the service had assigned. What stripping buys is that the document written is the document
described — a new item does not silently carry another item's identity.

### A map literal cannot be inserted

`INSERT INTO t (_MAP) VALUES (MAP['id', 'x'])` fails in the validator, and the limitation is Calcite's:

```
java.lang.UnsupportedOperationException: Unsupported type when convertTypeToSpec: ANY
```

Implicit coercion casts the source row to the target row type, and building a `SqlDataTypeSpec` for
`MAP<VARCHAR, ANY>` is unimplemented. An explicit `CAST` fails identically, for the same reason.

Recorded rather than worked around, because the shape that does work is the more useful one: a source
column already typed `MAP<VARCHAR, ANY>` needs no coercion at all, and a scan of another container is
exactly that. Copying documents between containers — the case the map column exists for — is
unaffected.

### Why the table declares column strategies

`INSERT` does not reach a rule without this, and the failure is at validation:

```
Column '_MAP' has no default value and does not allow NULLs
```

`SqlValidatorImpl.checkFieldCount` requires every column that is neither nullable nor defaulted to be
supplied. `_MAP` and `id` are both `NOT NULL` and both true — every document has an id, and the map
column is the document. The row type is not going to be weakened to admit a write; a row type that
bends to what a caller wants to omit is a suggestion rather than a declaration.

`ColumnStrategy` says the right thing instead, separating *not null in the table* from *optional in an
insert*. `CosmosTable` supplies an `InitializerExpressionFactory` reporting `DEFAULT` for `_MAP`, `id`
and the partition key columns — defaulting to `NULL`, which the rules above read as "not supplied" —
and `STORED` for `_ts` and `_etag`.

The columns a modify's input carries are unaffected by any of this: the input row type is always the
table's whole row type, with the omitted columns as typed null literals. Only the *arity* an `INSERT`
without a column list expects changes, the two `STORED` columns dropping out of it.

### Two rules are not bound to a convention, and neither can be

Every other rule here is created per `CosmosConvention`, because a convention is bound to a container
and some rules must consult that container's metadata. The write rule and the lookup join rule
cannot be, and the reason is a property of `ConverterRule`: its description is derived from the
traits it converts between. Both convert `NONE` to `CLR_ASYNC_ENUMERABLE`, neither of which names a
container, so every per-container instance carries the same description — and rules compare by
description, so a planner given two containers' rule sets keeps one and discards the rest.

**Measured twice, and the second measurement sharpened the first.** With a second container's rules
registered, an insert into the first stopped planning entirely: the surviving write-rule instance
was checking for the wrong convention and declined. The lookup join then showed why its own tests
had never caught the same defect — `addRule` does reject the later duplicates, but rejection does
not decide matching: in every measured plan the predicate that ran belonged to an instance `addRule`
had rejected, the one most recently constructed at the rule's *first* firing, and that instance
stayed bound for the rest of the run. `CosmosConvention.register` rebuilds the rule set per
convention and a join's inputs register left before right, so at a lone join's first match the
freshest instance is its own probe side's. Two containers therefore passed in either orientation by
an accident of registration order that a third container ends: the instance bound at the first join
judged the second join too, declined it, and that container was read whole, through a hash join,
silently.

The consequence for both rules is the same: an unbound rule is correct only if every instance is
interchangeable, so their predicates capture nothing and read everything from the matched node —
the container being named by the modify, or found beneath the join's probe side. There was nothing
for the binding to do anyway.

### Deleting, and reading first

`DeleteItemAsync` takes an `id` and a partition key, so a delete needs both per row, and a `WHERE`
clause that pins neither has to read the rows before it can delete them.

**Read-then-delete is allowed rather than refused, because the plan shows it.** The scan feeding the
modify is right there in the tree, and refusing would make `DELETE … WHERE price > 100` impossible
rather than expensive — a container is not less deletable for lacking a predicate over its key. Where
the predicate does pin `id` and a complete partition key the scan is already a point read, so the
cheap case falls out of work that is done.

What this does *not* do is the whole-partition case: a predicate pinning only the partition key could
be `DeleteAllItemsByPartitionKeyStreamAsync`, which is not a query at all. That is
`SupportsDeletePushDown` in the Flink table and is not attempted here.

**A delete needs the partition key as a value, and the promoted columns are where it comes from.** A
nested partition key path is not promoted, so it is read out of the map column instead; a container
whose key is nested is not therefore undeletable.

One more thing the rule must do, which nothing else here has needed: **simplify the input's trait
set.** A `Values` node advertises several collations at once — every ordering a single row trivially
satisfies — and asking such a trait set for its one collation throws. An `INSERT` whose source is
`VALUES` is the first statement anyone writes, so without it the rule fails immediately; a `DELETE`
never shows it, its input being a scan, which claims no collation at all.

### Updating

**SQL fixes the *what*; the adapter owns the *how*.** An `UPDATE` assigns whole values to named
columns, computed from the old row — there is no sub-path assignment in the grammar — and the
planner hands over the finished story: `updateColumnList`, `sourceExpressionList`, and the scanned
rows to evaluate them against. Any execution that lands that result is legitimate, chosen by cost.
That gives a ladder:

1. **Replace — implemented.** `SET "_MAP" = …` is whole-document assignment by its own words, and
   `ReplaceItemAsync` is that operation, priced as what it is. Not a stand-in for a patch: when the
   named column is the document, replacing the document is the faithful reading. The read this
   requires is the scan the plan already shows — the same argument recorded for deleting — and
   where the predicate pins `id` and a complete partition key that scan is already a point read.
2. **Patch for targeted `SET`s — waits on a decision.** A `SET` of a plain document property is
   `PatchItemAsync`'s native input, far cheaper than a replace. But no such column exists yet: the
   row model's columns are all identity, placement, service bookkeeping, or the document itself
   (the enumeration below). *Declared columns* — caller-declared, typed paths promoted to real
   columns — would be the targets; they are designed and parked on the `declared-columns-parked`
   branch pending the owner's decision.
3. **Static decomposition — future.** A mutation operator in the Cosmos table (`JSON_SET`-style,
   the way JSON-column databases spell copy-and-modify) would let a rule read patch operations
   straight off a `SET "_MAP" = JSON_SET(…)` expression at plan time.
4. **Optimizations, recorded not built.** A runtime diff of old against new document into patch
   operations is only equivalent to a replace under `If-Match`, and is bounded by the ten-operation
   patch limit; a *blind* patch — no read at all — is possible exactly when the predicate pins
   `id` plus the full key and every `SET` value is a literal.

**What a replace refuses, by enumeration.** `SET "id"` renames identity and `SET` of a partition
key path changes placement; the service forbids both on an existing document, so both are declined
at planning — a plan that fails once, rather than a request that fails per row. Honouring a
placement change would be a delete and a create, which is a different statement. `_ts` and `_etag`
are declared `STORED`, so the validator refuses them before any rule runs. The map column may still
*carry* a different identity or placement inside its value — invisible at plan time, and the
service rejects the resulting request loudly, which is the correct fate for it.

**Building the replacement document.** The row's table columns hold what the scan read; the `SET`
values trail them. The old values identify the target — `id` and the partition key are read out of
the document they describe, as a delete's are. For the body, the old promoted values are *withheld*
rather than copied when the map is being set: the document builder lets a non-null promoted column
override the map's entry, which is right for an insert and would here silently write old values
over whatever the new map says. `id` is the one exception kept, so a new map that omits it still
describes the same document, while one that contradicts it fails loudly at the service.

Two decisions the patch tier inherits when it lands:

- **`SET x = NULL` writes a JSON null** rather than removing the property. An `INSERT` skips null
  promoted columns because an *unmentioned* column arrives as null; an `UPDATE`'s
  `updateColumnList` names exactly what the statement wrote, so its null is explicit and is
  written.
- **No `If-Match`.** No write sends an ETag, matching `DELETE`: the read informs rather than
  locks, last write wins, and optimistic concurrency is a session-level surface this adapter does
  not invent. Under that stance a replace is the honest reading of `SET "_MAP"` — the document
  becomes what was computed from what was read.

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

Everything above is *inference* — from declared metadata and, where the service gave one, a
measured row count. The service reports what a request actually cost, and that number is the only
one in the system that is not a guess.

---

## What the adapter reports

`CosmosInstrumentation` publishes a `Meter` and an `ActivitySource`, both named
`Apache.Calcite.Cosmos.Adapter`.

**Through .NET rather than through Calcite**, because Calcite has nowhere to put it. Every `Hook`
value is plan-time — `PARSE_TREE`, `CONVERTED`, `TRIMMED`, `PROGRAM`, `QUERY_PLAN` — and no adapter
in the tree reports execution statistics through one. Cassandra, Druid, Elasticsearch, Geode and
MongoDB all use `Hook.QUERY_PLAN` and stop, which this adapter does too. A meter and an activity
source are what a .NET caller already has a collector for, cost nothing when nobody is listening,
and require no coupling to this assembly.

| | |
|---|---|
| `cosmos.request_charge` | Request units, one measurement per response |
| `cosmos.responses` | Responses received |
| `cosmos.query` (span) | One statement, first request to last page |

Both instruments are tagged with `cosmos.container` and `cosmos.request_kind`, the latter being
`query` or `point_read`. The kind is what makes the point read visible at all: it is charged and
counted like any other request, and without the tag it cannot be told from the query it replaced.

Per *response* rather than per execution, because a query spanning continuations is charged per
page and the spread across pages is itself worth seeing. The span carries the totals —
`cosmos.request_charge` and `cosmos.pages` — since that is what one reader of one trace wants. A
span that never records them is an enumeration the caller abandoned, which is its own signal.

### Index metrics

`PopulateIndexMetrics`, behind the `indexMetrics` operand and off by default: the service computes
the answer per query, so it is a thing to switch on while working out why a query is expensive
rather than to leave on. It lands on the span as `cosmos.index_metrics`, a tag rather than a
measurement, because it is a paragraph of prose naming indexes — something to read, not aggregate.

It is also the instrument for settling the composite index question below.

---

## Unvalidated assumptions

Recorded so they are not mistaken for tested behaviour.

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
