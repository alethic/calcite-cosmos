# Outstanding work

What a complete adapter would have, sized and reasoned, so that the next session picks up an argument
rather than a list.

**Sizes.** *Small* is a translator case and a test. *Medium* is a node, a rule, or an SDK surface.
*Large* needs a design decision recorded in `DESIGN.md` before any code.

**On evidence.** Where a claim about the service is unverified it says so. The emulator has disagreed
with Azure in both directions — accepting an `ORDER BY` over an unnest alias that Azure rejects, and
rejecting the full text search Azure runs — so "the reference says" is not a measurement. Point
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` at a real account and the suite runs against one.

---

## 1. Statistics and the cost model

Today `getStatistic` reports keys and collations derived from declared metadata, no row count, and the
cost model is pure inference. The service will answer far more than that, and every item here is a
read of something it already knows.

### Row count and document size — *small*

`ReadContainerAsync().Headers.Get("x-ms-resource-usage")` carries `documentsCount`, `documentsSize`
and `collectionSize`. A row count is the single most load-bearing number a planner has, and this one
is free, approximate and lagging — which is exactly what a planner row count is allowed to be.
Document size feeds the other half: what a row *costs to move*, which for a map row model carrying
whole documents is the dominant term and is currently not modelled at all.

### Physical partition count — *small*

`GetFeedRangesAsync` returns one feed range per physical partition. That is the fan-out factor: a
cross-partition query costs roughly that many single-partition queries, and nothing prices it. It is
also what makes the partition-key discount meaningful rather than a constant — pinning the key on a
two-partition container saves little, and on a two-hundred-partition container saves nearly
everything.

### Provisioned throughput — *small*

`ReadThroughputAsync`. Not needed to compare two plans, but it is the denominator that turns an RU
estimate into a latency estimate, and it distinguishes a container that can absorb a scan from one
that cannot.

### `RequestCharge` — *medium*

The only real cost signal the service gives, on every response, and currently discarded. Wants
somewhere to go — see *Observability*, which is the design decision.

### `PopulateIndexMetrics` — *medium*

Reports which indexes a query actually used, and which it recommends. Two uses, the second more
interesting than the first: it would settle the composite-index question below by measurement, and it
is the raw material for telling a user *why* a query was expensive.

### Per-partition skew — *not available*

Per-partition storage is an Azure Monitor metric, not data plane. The count is reachable and the
distribution is not, so a hot-partition estimate would have to come from outside the adapter.

### A cost model in RU — *large*

The above are inputs; this is the model. Cosmos charges in RUs and the current model multiplies
Calcite's abstract cost by constants. A model in RUs — a point read is 1, a query is 2.3 plus scanned
size, a cross-partition query is that times the fan-out — would make pushdown decisions comparable
with in-process alternatives on a real scale rather than a notional one.

---

## 2. Execution paths

Every execution today is `GetItemQueryStreamIterator`. The SDK has cheaper routes and the adapter uses
none of them.

### Point lookup by `id` and partition key — *done*

`ReadItem` where the predicate is exactly an `id` and a complete partition key, and the rest of the
statement asks for nothing a read cannot answer. The converter carries a second row builder that walks
paths in the returned document, because a read returns the document rather than the projection.

### Batch point reads for `id IN (…)` — *medium, after the above*

`ReadManyItemsAsync` takes (id, partition key) pairs and is charged as point reads. Same recovery one
level up, nearly free once the single point read exists.

### `MaxConcurrency` and `MaxBufferedItemCount` — *small*

`MaxItemCount` comes from a pushed-down limit; the fan-out knobs are untouched. A query with the
partition key pinned needs no concurrency; a cross-partition one wants it, and how much depends on the
partition count above.

### Change feed — *large*

`GetChangeFeedIterator` is a fundamentally different read: ordered by `_ts` within a partition,
resumable, and the basis of every incremental pipeline built on Cosmos. It is not a table in the
relational sense — it has no end — so exposing it means deciding what it *is* to Calcite: a table
function taking a start time, a streaming source, or something a caller drives and the adapter only
materializes.

### Continuation tokens — *medium*

A query's continuation token makes a result resumable, and the adapter reads every page eagerly within
one enumeration. `GROUP BY` and `DISTINCT` results are documented as not resumable, which is a
constraint on where this can apply rather than a reason not to.

---

## 3. Writing

**Cosmos SQL has no DML, and this is not a reason the adapter cannot write.** The query language has no
`INSERT`, `UPDATE` or `DELETE`, but the SDK has item CRUD, and Calcite expresses writes through
`ModifiableTable` rather than through generated SQL. This is the largest missing *category* — the
adapter is currently read-only and nothing about Cosmos requires that.

### `INSERT` — *large*

`ModifiableTable.getModifiableCollection`, or an `EnumerableTableModify` equivalent for the async
convention, over `CreateItemAsync`/`UpsertItemAsync`. The row model makes it interesting: a document is
the map column, so an insert of promoted columns alone is an incomplete document, and an insert of the
map column is the document itself. Which one a caller means has to be decided.

### `DELETE` — *medium, after INSERT*

`DeleteItemAsync` given `id` and the partition key — the same recovery the point lookup needs, so it
falls out of that work. `WHERE` clauses that do not pin both would have to read then delete, which is a
different cost and should probably be refused rather than done silently.

### `UPDATE` — *large*

Cosmos has patch operations (`PatchItemAsync`) that map onto a targeted `SET`, and replace for the
rest. A patch is far cheaper than a read-modify-write and expresses a bounded set of operations, so the
translation from `SET` clauses to patch operations is the interesting part.

### Transactional batch — *medium*

`TransactionalBatch` is atomic within a single partition key. That is a real transactional guarantee
Calcite has no way to ask for, so exposing it means a session-level or hint-level surface rather than
SQL.

### Bulk mode — *small*

`CosmosClientOptions.AllowBulkExecution` changes the throughput profile of many small writes
dramatically. A client factory can already set it; whether the adapter should is a question about who
owns the client.

---

## 4. Query language coverage

### Ranking and search

- **Full text** — `FULLTEXTCONTAINS`, `ALL`, `ANY`, `FULLTEXTSCORE`, `RRF`, `ORDER BY RANK`. *Done.*
- **Vector** — `VECTORDISTANCE`, rankable and projectable, fusing with `RRF` for hybrid search. *Done.*
- **Spatial** — *medium.* `ST_DISTANCE`, `ST_WITHIN`, `ST_INTERSECTS`, `ST_ISVALID`. Calcite has a
  spatial operator library to map from, so the mapping is mechanical; the geometry representation and
  what a spatial index makes cheap are not.

### Subqueries

- **`EXISTS` over an item-scoped subquery** — *large.* `EXISTS (SELECT VALUE t FROM t IN c.tags WHERE …)`
  is a semi-join over a nested array. Today the only route to a nested array is `Unnest`, which
  cross-products the document with it and de-duplicates above — the wrong shape and the wrong cost for
  an existence test.
- **Scalar and multi-value subqueries** — *medium.* Item-scoped only; there are no derived tables. The
  correlated forms are what `ARRAY(SELECT …)` and `IN (SELECT …)` need.

### Scalar functions still to map

- *Small each*: `ARRAY_SLICE`, `ARRAY_CONCAT`, `SETINTERSECT`, `SETUNION`; `LEFT`, `RIGHT`, `REVERSE`,
  `REPLICATE`, `REGEXMATCH`; the JSON conversions `ToString`, `StringToNumber`, `StringToObject`,
  `ObjectToArray`.
- **`LIKE` to `STARTSWITH`** — *small, and the valuable one.* A `LIKE` whose pattern is a literal with
  a single trailing wildcard is a prefix match, which the index serves; a general `LIKE` is a scan.
- **Currently declined, admissible with work** — `SUBSTRING` without a length (`LENGTH(s)` supplies
  it); `LIKE` with `ESCAPE`; `TRIM` of a non-space character and `TRUNCATE` to decimal places, both
  needing Cosmos's two-argument arity **verified** first; `IS TRUE`/`IS FALSE`/`IS DISTINCT FROM`,
  expressible with the `??` operator once the null-versus-undefined semantics are measured.

### Temporal — *large*

Cosmos has `DateTimeAdd`, `DateTimeDiff`, `DateTimePart`, `DateTimeBin` and tick conversions; Calcite
has `EXTRACT`, `TIMESTAMPADD`, `TIMESTAMPDIFF`. The mapping is mechanical and the representation is
not: a date is an ISO string or an epoch number by application convention, and `_ts` is the only value
whose encoding the service defines. Pushing a temporal function down means deciding what the column
*is*, which is the thing this adapter refuses to guess everywhere else.

### Clause-level

- **Native `IN` and `BETWEEN`** — *small.* `expandSearch` turns both into comparison chains, which is
  what makes them pushable. The reference calls `IN` index-friendly; whether the OR-chain is priced
  the same is worth measuring before doing this for its own sake.
- **`SELECT DISTINCT`** — *small.* `Query.Distinct` is never set. An aggregate with no calls already
  renders as `GROUP BY` over every key, which is a correct `DISTINCT`, so this is not a coverage gap;
  what it buys is the `ORDER BY` combination, since `GROUP BY` and `ORDER BY` cannot coexist. Whether
  `DISTINCT` and `ORDER BY` can is **unverified**.
- **`TOP`** — *small.* Emitted for a rank clause and nowhere else; `OFFSET`/`LIMIT` covers the rest.

---

## 5. Planner

### Half a join — *large, highest value after the point lookup*

A relational join is not expressible in Cosmos, so both sides are read whole and joined in process.
One side can pay for the other: evaluate `other`, collect the distinct values of its key, and push
`cosmos.k IN (…)` into the Cosmos side. The reference points the same way from the other end — its
documented workaround for a join is to inline a literal array of reference data.

**Sound for the same reason partial filter pushdown is.** Every row surviving the join satisfies
`k IN (values)`, so the pushed predicate is implied by the join condition and can only discard rows
that would not have joined.

Three things make it work rather than merely be correct:

- **It is a run-time value.** The plan cannot hold the statement, because the values are not known
  until the build side is read. Everything renders once at prepare today, so this is the design
  decision the feature turns on.
- **It needs a cardinality bound.** Ten values is a large win; a million is a statement the service
  refuses on length. The bound wants measuring.
- **It wants the partition key.** If the pushed key *is* the partition key this collapses a fan-out
  into a handful of single-partition reads, and with `id` too, into the batch point read.

Calcite calls the general shape sideways information passing; the same machinery serves
`IN (subquery)`.

### Weakening a disjunction — *medium*

Dropping a conjunct weakens; dropping a disjunct strengthens, so an `OR` is pushable only when every
branch is. It is still pushable when every branch can be *weakened* — `a OR b` with `b`
untranslatable can push `a OR <something b implies>`, and `IS_DEFINED` of the paths `b` mentions is
often such a thing.

### Smaller rules

- **Filter splitting above a projection** — *small.* `CosmosFilterSplitRule` matches `Filter` over
  `TableScan` only; a filter over a projection is the same split and the same argument.
- **Sorting above an aggregate** — *small.* `GROUP BY` and `ORDER BY` cannot coexist, so a sorted
  aggregate declines entirely. The aggregate could push and the sort stay in Calcite, over what is by
  then a small result.
- **Unique key policy** — *small.* Declared unique keys are keys `getStatistic` does not report.
- **Computed properties** — *medium.* A container can declare named, queryable, indexable computed
  paths. Declared metadata is the one kind this adapter trusts, so they should promote to real columns
  with real index awareness rather than living in the map column.

### Recorded decisions worth revisiting

- **`SELECT VALUE` for a single column** — `DESIGN.md` chose the uniform object form deliberately,
  "whatever the arity", and the materializer depends on it. A single-column projection could be bare
  scalars. Reversing a recorded decision is the work; the code is trivial.
- **`SELECT *` sends promoted columns twice** — `_MAP` is the whole document and every promoted column
  is a path within it. Reading them out of the map value client-side would avoid it; the saving is a
  few short scalars against a whole document, so smaller than it first looks.

---

## 6. Row model and types

- **Type coverage** — done and verified against a live account: strings, whole and fractional numbers,
  booleans, nulls, objects, arrays, to arbitrary depth, as declared types and as `ANY`.
- **Binary** — *small.* `BINARY`/`VARBINARY` read base64 from a JSON string. Unverified against the
  service, because nothing in the test data is binary.
- **Temporal representation** — see *Temporal* above. The reading side handles ISO strings and epoch
  numbers; what is missing is any basis for deciding which a column holds.
- **Hierarchical partition keys** — *done.* A pinned leading run routes on the prefix; an inner path
  without the one above it narrows nothing and is not offered as though it did. A prefix routes and
  does not identify a document, so it cannot carry a point read — `PartitionKeyIsComplete` is what
  keeps those apart.

---

## 7. Provider and integration

- **Client factory** — *done.* `clientFactory` names an `ICosmosClientFactory`, which subsumes Entra
  ID, custom serializers, retry policies, preferred regions and a DI-owned client.
- **Schema functions** — *medium, mis-sized before.* The validator resolves a name against the `fun`
  operator table chained with the catalog reader, and the catalog reader resolves the schema's own
  functions — so `CosmosOperators.Instance` being a `SqlOperatorTable` is why full text is unreachable
  from a `CalciteConnection`. Registering them means `ScalarFunctionImpl` over real CLR methods, and
  these functions cannot execute outside Cosmos, so those methods would throw: a non-pushed query
  becomes a run-time failure rather than a plan-time refusal. That trade is the decision.
- **Connection options as operands** — *small.* Consistency level, preferred regions, application name,
  for callers who do not want to write a factory.
- **Multiple databases** — *done.* Omitting `database` exposes the account, with one subschema per
  database, which is how both Cosmos and Calcite nest. Metadata for every container of every database
  is read eagerly when the schema is built — a container definition is small and Calcite asks for the
  whole map at once, but an account with many databases pays for all of them to reach one. Lazy
  subschemas would fix that and want a lazy `Map`.
- **Client disposal** — *small.* The schema owns a client for the life of the process because Calcite
  offers no disposal hook. Worth revisiting against `SchemaPlus` rather than left as a comment.
- **Server-side functions** — *medium.* Cosmos has stored procedures and JavaScript UDFs. A UDF is
  nameable in a query, so it could be exposed as a Calcite operator the way the built-ins are.

---

## 8. Observability

- **A diagnostics surface** — *medium, and it gates two items above.* `RequestCharge`, `IndexMetrics`
  and `CosmosDiagnostics` all have somewhere to come from and nowhere to go. Calcite's `Hook` is one
  candidate, an event on the schema another, `ActivitySource` a third. Deciding is the work.
- **`QUERY_PLAN` hook** — *done.* The converter already runs it with the rendered statement.
- **RU regression tracking** — *medium, after the charge is surfaced.* Assert that a query shape does
  not get more expensive.

---

## 9. Testing

- **Acceptance suite in CI** — *done.* Every emitted form executes against an emulator on the Linux
  leg.
- **A real account in CI** — *medium.* The emulator accepts statements Azure rejects and rejects
  features Azure implements; both were found by hand this session. A nightly job against a real
  account is what stops the next one being found by a user.
- **Differential testing** — *large, and the highest-value test work.* Run the same SQL twice — once
  with the Cosmos rules registered, once with them withheld so Calcite evaluates everything in
  process — and require the same rows. Every pushdown is then checked against an oracle rather than
  against an expected string. This is what `ClrEnumerableDifferentialTests` does for the CLR
  conventions in `calcite-dotnet`, and it is where the defects were found there.
- **Emulator gaps, recorded** — *small.* The emulator silently discards composite indexes and does not
  implement full text search. Both are known; neither is asserted, so a future emulator that fixes
  them would go unnoticed.

---

## 10. Unsettled questions

These are not features. They are things believed but not measured, and each one is a defect waiting
for the right query.

- **The composite index requirement.** A multi-key `ORDER BY` is refused without a matching composite
  index. Documented, never observed — the emulator implements composite indexes not at all.
  `PopulateIndexMetrics` against a real account would settle it.
- **`DISTINCT` with `ORDER BY`.** Assumed incompatible, never tested.
- **Two-argument `TRIM` and `TRUNCATE`.** Left out for want of a measurement.
- **Out-of-domain arithmetic.** Measured: `ASIN(2)`, `ACOS(2)`, `SQRT(-1)` and `LOG(0)` each fail the
  whole query where Calcite yields NaN. Pushed anyway, consistently, and recorded so the consistency
  is a decision.
