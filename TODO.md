# Outstanding work

Sized and reasoned, so that the next session picks up an argument rather than a list. Where a claim
about the service is unverified it says so — the emulator has twice disagreed with Azure, in both
directions, so "the docs say" is not a measurement.

**Sizes.** *Small* is a translator case and a test. *Medium* is a node or a rule. *Large* needs a
design decision recorded in `DESIGN.md` before code.

---

## Cost — what the service charges

### Point lookup by `id` and partition key — *medium, highest value*

A point read is ~1 RU. The same fetch as a query is 2.3 RU minimum and goes through the query engine.
For key-value access, which is a large share of Cosmos usage, this is the biggest saving available and
nothing in the adapter does it: every execution is `GetItemQueryStreamIterator`, and no `ReadItem` call
exists anywhere.

Half the machinery is already here — `CosmosPartitionKeyExtractor` recovers the partition key values
and `CosmosQuery` carries them. What is missing is recovering an `id` equality alongside them and
choosing `ReadItem`.

The design work is not the SDK call. **A point read returns the document, not the projected object**,
and the converter's materializer assumes every row is an object keyed by output field name. Either the
point-read path carries its own row builder — reading promoted columns out of the document root, the
shape the map row model started from — or the query path stops projecting. The first is smaller.

### Batch point reads for `id IN (…)` — *medium, after the above*

`ReadManyItemsAsync` takes a list of (id, partition key) pairs and is charged as point reads. The
predicate shape is the same recovery one level up: an `IN` over `id` with the partition key pinned.
Worth nothing until the single point read exists, and nearly free after it.

### Row count from the container — *small*

`getStatistic` returns no row count, so every cost is relative. `ContainerProperties` and the quota
headers expose a document count. It is approximate and lags, which is exactly what a planner row count
is allowed to be, and it would let the cost model distinguish a container of a thousand documents from
one of a billion.

### `RequestCharge` and `PopulateIndexMetrics` — *medium*

The cost model is pure inference from declared metadata. `RequestCharge` is the only real signal the
service gives, and `PopulateIndexMetrics` reports which indexes a query actually used — which would
settle the composite index question below by measurement instead of by reading the documentation
again. Both need somewhere to go: there is no diagnostics surface, and inventing one is the design
decision.

### `MaxConcurrency` and `MaxBufferedItemCount` — *small*

`MaxItemCount` is set from a pushed-down limit; the other two fan-out knobs are untouched. A query the
partition key pinned needs no concurrency at all, and a cross-partition one wants it.

### Cross-partition fan-out in the cost model — *medium*

`CosmosFilter` discounts a pinned partition key. Nothing costs the opposite case, so a plan that fans
out across every physical partition is priced as though it did not.

---

## Query language — what Cosmos can express that we do not emit

### Vector search — *small, and nearly free now*

`VectorDistance` is the third scoring function `ORDER BY RANK` accepts, and `CosmosRank` does not care
which one it renders. An operator definition and a translator case. It composes with `RRF` for hybrid
search, which already works for two full text scores.

### Spatial — *medium*

`ST_DISTANCE`, `ST_WITHIN`, `ST_INTERSECTS`, `ST_ISVALID`. Calcite has a spatial operator library to
map from, so this is a mapping exercise rather than a new operator surface — but the geometry
representation has to be settled, and a spatial index changes what is cheap.

### `EXISTS` over an item-scoped subquery — *large*

`EXISTS (SELECT VALUE t FROM t IN c.tags WHERE …)` is a semi-join over a nested array. Today the only
way to reach a nested array is `Unnest`, which cross-products the document with it and then
de-duplicates above — for an existence test that is the wrong shape and the wrong cost. Needs a rule
matching the correlated-semi-join plan and a subquery form in the builder.

### Native `IN` and `BETWEEN` — *small*

`RexUtil.expandSearch` turns both into comparison chains, which is what makes them pushable at all.
Cosmos has both keywords natively and the reference calls `IN` index-friendly. Whether the OR-chain is
priced the same is worth measuring before doing this for its own sake.

### `SELECT DISTINCT` — *small*

`CosmosQueryBuilder.Distinct` is never set. An aggregate with no calls already renders as `GROUP BY`
over every key, which is a *correct* `DISTINCT`, so this is not a coverage gap. What it would buy is
the `ORDER BY` combination, since `GROUP BY` and `ORDER BY` cannot coexist — and whether `DISTINCT`
and `ORDER BY` can is **unverified**.

### String functions — *small each*

`STARTSWITH` / `ENDSWITH` / `CONTAINS` from a `LIKE` whose pattern is a literal with one wildcard at a
known end; `REGEXMATCH`; `LEFT`, `RIGHT`, `REVERSE`, `REPLICATE`. The `LIKE` rewrite is the valuable
one — a prefix match is index-friendly where a general `LIKE` is not.

### Currently declined, and could be admitted — *small each*

- `SUBSTRING` without a length. Cosmos requires one; `LENGTH(s)` supplies it.
- `LIKE` with `ESCAPE`. Refused rather than dropped, which is right; translating the escape is possible.
- `TRIM` of a character other than a space, and `TRUNCATE` to decimal places. Both need the arity of
  Cosmos's two-argument forms **verified** first — they were left out for exactly that reason.
- `IS TRUE` / `IS FALSE` / `IS DISTINCT FROM`. Expressible with the `??` coalesce operator, but the
  null-versus-undefined semantics need measuring rather than reasoning about.

### Temporal — *large*

Cosmos has `DateTimeAdd`, `DateTimeDiff`, `DateTimePart`, `DateTimeBin` and the tick conversions, and
Calcite has `EXTRACT`, `TIMESTAMPADD`, `TIMESTAMPDIFF`. The mapping is mechanical; what is not is the
representation. Cosmos stores a date as an ISO string or an epoch number by application convention,
and `_ts` is the only value whose encoding the service defines. A row type cannot know which, so
pushing a temporal function down means deciding what the column is — which is the thing this adapter
refuses to guess at everywhere else.

### The JSON conversion family — *small*

`ToString`, `StringToNumber`, `StringToObject`, `ObjectToArray`. No Calcite counterpart, so they would
be adapter operators like the type tests.

### Array functions — *small*

`ARRAY_SLICE`, `ARRAY_CONCAT`, `SETINTERSECT`, `SETUNION`. `ARRAY_LENGTH` and `ARRAY_CONTAINS` are
done.

### Computed properties — *medium*

A container can declare computed properties: named, queryable, indexable paths. They are declared
metadata, which is the one kind of thing this adapter is willing to trust, and they would promote to
real columns with real index awareness rather than living in the map column.

---

## Planner

### Filter splitting above a projection — *small*

`CosmosFilterSplitRule` matches `Filter` over `TableScan` only. A filter over a projection is the same
split and the same soundness argument.

### Half a join — *large, and the most valuable thing on this page after the point lookup*

A relational join is not expressible in Cosmos, so today it is evaluated entirely in Calcite: both
sides are read whole and joined in process. But one side can pay for the other. Given
`cosmos JOIN other ON cosmos.k = other.k`, evaluate `other` first, collect the distinct values of its
key, and push `cosmos.k IN (…)` into the Cosmos side. The join still happens in Calcite; what changes
is how many documents cross the wire to reach it.

The reference points the same way from the other end: its documented workaround for a relational join
is to inline a literal array of reference data into the query. This is that, derived rather than
hand-written.

**Sound for the same reason partial filter pushdown is.** Every row that survives the join satisfies
`k IN (values)`, so the pushed predicate is implied by the join condition and can only discard rows
that would not have joined. It is a weakening, and the residual join is unchanged.

Three things make it work rather than merely be correct:

- **It is a run-time value, not a plan-time one.** The plan cannot hold the statement, because the
  values are not known until the build side has been read. Either the statement is parameterised and
  the parameter list is built per execution, or the statement is rendered at execution. Everything in
  the adapter currently renders once, at prepare, so this is the design decision the feature turns on.
- **It needs a cardinality bound.** A build side of ten values is a large win; one of a million is a
  statement the service will refuse on length alone. Below the bound it fires, above it the join
  proceeds as it does today, and the bound wants measuring rather than picking.
- **It wants the partition key.** If the pushed key *is* the partition key, this collapses a
  cross-partition fan-out into a handful of single-partition reads — and with `id` as well, into the
  batch point read above.

Calcite calls the general shape sideways information passing; the same machinery would serve `IN` over
a sub-query, which is the same thing wearing different syntax.

### Weakening a disjunction — *medium*

Dropping a conjunct weakens; dropping a disjunct strengthens, so an `OR` is pushable only when every
branch is. It is still pushable when every branch can be *weakened* — `a OR b` where `b` is
untranslatable can push `a OR <something implied by b>`, and `IS_DEFINED` of the paths `b` mentions is
often such a thing. Sound, and worth doing only if a real query shape wants it.

### Sorting above an aggregate — *small*

`GROUP BY` and `ORDER BY` cannot coexist, so a sorted aggregate declines entirely. The aggregate could
push and the sort stay in Calcite, over what is by then a small result.

### `SELECT VALUE` for a single column — *small, but a recorded decision*

`DESIGN.md` chose the uniform object form deliberately, "whatever the arity", and the materializer
depends on it. A single-column projection could be `SELECT VALUE c.id` — bare scalars rather than a
one-property object per row. Reversing a recorded decision is the work; the code is trivial.

### `SELECT *` sends the promoted columns twice — *small*

`_MAP` is the whole document and every promoted column is a path within it, so `SELECT VALUE { _MAP: c,
id: c.id, … }` sends `id` twice. Reading the promoted columns out of the map value client-side would
avoid it. The saving is a few short scalars against a whole document — real, but smaller than it first
looks.

### Unique key policy — *small*

Declared unique keys are keys the planner could use, and `getStatistic` does not report them.

---

## Provider and integration

### Register the operators as schema functions — *small*

The validator resolves a name against the `fun` operator table chained with the catalog reader, and the
catalog reader resolves the schema's own functions. `CosmosOperators.Instance` is a `SqlOperatorTable`,
which is not that form, so full text is unreachable from a `CalciteConnection` — a hand-built planner
only. `CosmosSchema` extends `AbstractSchema`, so `getFunctionMultimap` closes it.

### Supplying the client — *small, and it subsumes several of these*

`CosmosSchemaFactory` builds its own `CosmosClient` from an endpoint and a key, so everything a
connection string cannot say is out of reach: an account reached with `TokenCredential`, a custom
serializer or retry policy, preferred regions, a client owned by a dependency injection container and
shared with the rest of the application rather than created per schema. A key in a model file is also
a key in a model file.

A `clientFactory` operand naming an `ICosmosClientFactory` closes all of it at once, and is the same
shape the model already uses to name the schema factory itself. Entra ID then needs no operand of its
own — a factory does it.

Code-first callers can already supply their own executor through `CosmosSchema`; it is the model path
that is key-only.

### Connection options as operands — *small*

Consistency level, preferred regions, application name, for callers who want them without writing a
factory. Connection mode is already an operand.

### The client is never disposed — *small, known*

The schema owns a `CosmosClient` for the life of the process because Calcite offers no disposal hook.
Worth revisiting against `SchemaPlus` rather than left as a comment.

---

## Testing

### The acceptance suite does not run in CI — *small, and it is the one that matters*

The workflow starts no emulator and names no account, so `EveryEmittedFormIsAcceptedByTheService` — the
test that caught the unnest defect — is inconclusive on every run. Everything else on this page is
worth less than making this true.

### A real account in CI — *medium*

The emulator accepts statements Azure rejects and rejects features Azure implements. Both were found by
hand. A nightly job against a real account is what stops the next one being found by a user.

### Differential testing — *large, and the highest-value test work*

Run the same SQL twice — once with the Cosmos rules registered, once with them withheld so Calcite
evaluates everything in-process — and require the same rows. Every pushdown is then checked against an
oracle rather than against an expected string. This is what `ClrEnumerableDifferentialTests` does for
the CLR conventions in `calcite-dotnet`, and it is where the defects were found there.

### RU regression tracking — *medium, after `RequestCharge`*

Assert that a query shape does not get more expensive. Needs the charge surfaced first.
