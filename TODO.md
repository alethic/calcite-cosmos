# Outstanding work

What a complete adapter would have, sized and reasoned, so that the next session picks up an argument
rather than a list.

**Sizes.** *Small* is a translator case and a test. *Medium* is a node, a rule, or an SDK surface.
*Large* needs a design decision recorded in `DESIGN.md` before any code.

**On testing a change.** Before believing a test covers what it claims, check that it *fails without
the change*. This caught two things in one session: a filter-split test that genuinely depended on
its fix, and a guard added to `CosmosSortRule` that turned out to be dead code — the case it guarded
was already unreachable, and removing it changed nothing. Same probe, opposite conclusions, and
neither was visible from a green suite.

**On evidence.** Where a claim about the service is unverified it says so. The emulator has disagreed
with Azure in both directions — accepting an `ORDER BY` over an unnest alias that Azure rejects, and
rejecting the full text search Azure runs — so "the reference says" is not a measurement. Point
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` at a real account and the suite runs against one.

---

## 0. Where this stands, and the one thing a host must do

408 passing, 5 skipped, on net8.0 and net10.0. Every emitted statement form is executed against a
live service; the suite runs against a real account when `COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY`
name one, and reports inconclusive rather than passing where the emulator cannot answer.

**A host must run the calc rules as a pass after the planner.** This is the one integration
requirement the adapter adds, and it is easy to miss because the failure names nothing useful — a
plan that cannot be implemented, with no indication of what is missing:

```csharp
var program = new HepProgramBuilder();
foreach (var rule in ClrAsyncEnumerableRules.CalcRules())
    program.addRuleInstance(rule);
```

It is Calcite's `Programs.CALC_PROGRAM` and it is a *pass*, not rules for the planner — given to
Volcano it does nothing, because `ClrAsyncEnumerableProject` is the cheaper node and also the one that
throws when implemented. Without the pass, a projection above a join has nothing to implement it. It
does not arise without a join, because every other projection is pushed into the container. See
section 11.

**Do not start the disjunction weakening without measuring first.** The analysis is done (section 4),
but two facts about the service decide *soundness* rather than speed: how it evaluates `NOT undefined`,
and whether `= null` is a comparison or a definedness test. The failure mode is a strengthened
predicate — rows lost, a smaller answer returned as though it were the answer.

**Nothing else is half-finished.** The lookup join, the diagnostics surface, and the statistics work
are complete and covered; what remains below is not started rather than in progress.

---

## 1. Statistics and the cost model

Today `getStatistic` reports keys and collations derived from declared metadata, no row count, and the
cost model is pure inference. The service will answer far more than that, and every item here is a
read of something it already knows.

### Row count and document size — *done*

Read from the container's `x-ms-resource-usage` header into `CosmosContainerStatistics`, and reported
as `getStatistic().getRowCount()`. Measured: the count lags badly enough to report zero immediately
after documents are written, which is what a planner row count is allowed to be. Average document size
is derived and not yet used — it is what a row costs to move, which for a map row model carrying whole
documents dominates, and wiring it into the cost model is the next step.

### Physical partition count — *done*

`GetFeedRangesAsync` returns one feed range per physical partition, and `CosmosFilter` now discounts
by it: a complete key divides the work by the partition count, and a prefix takes the square root of
that, which is a guess about cost and cannot affect which rows come back. Where the service was not
asked the old constant stands.

### Provisioned throughput — *small*

`ReadThroughputAsync`. Not needed to compare two plans, but it is the denominator that turns an RU
estimate into a latency estimate, and it distinguishes a container that can absorb a scan from one
that cannot.

### `RequestCharge` — *done*

The only real cost signal the service gives, on every response. Recorded on the
`cosmos.request_charge` histogram, tagged by container and by whether the request was a query or a
point read, and totalled onto the `cosmos.query` span. See *Observability*.

Still open: **feeding it back into the cost model.** A measured charge for a query shape is worth
more than the estimate that was used to choose it, but a cost model that learns needs somewhere to
keep what it learnt, and that is the statistics-refresh question above wearing a different hat.

### `PopulateIndexMetrics` — *done*

Behind the `indexMetrics` operand, off by default, surfaced as `cosmos.index_metrics` on the span.
It serves the more interesting of its two uses — raw material for telling a user *why* a query was
expensive. The emulator returns nothing for it, so the test asserting it is inconclusive there and
passes against a real account.

The other use, settling the composite-index question, turned out not to need it: the question is
answered directly by whether the service accepts the statement, and it does not. See *Unsettled
questions*, where it is no longer unsettled.

### Per-partition skew — *not available*

Per-partition storage is an Azure Monitor metric, not data plane. The count is reachable and the
distribution is not, so a hot-partition estimate would have to come from outside the adapter.

### Statistics refresh — *medium*

Fetched once per container, on first use, and never again: a schema that lives for the life of a
process will plan against a row count from whenever it was first asked. A time-to-live, or an explicit
refresh, is the missing piece. Drill's answer is a metastore that `ANALYZE TABLE COMPUTE STATISTICS`
populates, which decouples the fetch from the query entirely and is worth considering over a TTL.

### Statistics after pushdown — *large*

Flink collects connector statistics *after* partition pruning and filter pushdown, so the number the
planner sees describes the scan it will actually do rather than the whole table. Here that would mean
a row count for a partition-pinned scan rather than for the container — which is the difference
between costing a single-partition read and costing everything. It needs a statistic attached to a
`RelNode` rather than to a table, which is a larger change than it sounds.

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

### `MaxConcurrency` — *done*; `MaxBufferedItemCount` — *left alone, deliberately*

A query pinned to a partition key now asks for no concurrency: one logical partition lives on one
physical partition, so there is nothing for a second worker to read, and the SDK otherwise sizes its
fan-out for a query that might span every partition.

A cross-partition query is left at the SDK's default. How much concurrency it wants depends on the
container's spread, and a constant invented here would be a worse guess than the SDK's own — the
partition count is known to the metadata but not to the executor, and plumbing it through is only
worth doing with a measurement to point at.

`MaxBufferedItemCount` is untouched for the same reason, with less to gain: it bounds a buffer whose
right size depends on row size and consumer speed, neither of which this can see.

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

### Half a join, as a lookup join — *large, highest value after the point lookup*

A relational join is not expressible in Cosmos, so both sides are read whole and joined in process.
One side can pay for the other: take the join keys of the build side and fetch only the documents that
could match. The reference points the same way from the other end — its documented workaround for a
join is to inline a literal array of reference data.

**Sound for the same reason partial filter pushdown is.** Every row surviving the join satisfies
`k IN (values)`, so the restriction is implied by the join condition and can only discard rows that
would not have joined.

**The precedent is out of tree, and it is Flink's lookup join.** No in-tree storage adapter does
anything with a join — Cassandra, MongoDB, Elasticsearch, Geode and Druid read both sides and let
Calcite join them, and the only in-tree adapter that touches `RexCorrelVariable` is JDBC, which can
express a correlated subquery natively in SQL.

Flink's shape is the one to copy, and the important part is what it does *not* do:

- The correlation variable is a **planner-level device only**. `LogicalCorrelate` is what the rules
  match, and `LogicalCorrelateToJoinFromTemporalTableRule` rewrites it away — its `decorrelate()`
  rewrites every `RexCorrelVariable` reference as a `RexInputRef` against the combined row type. No
  correlation variable survives into a physical node, which is why no adapter ever renders one.
- The planner tells the source **which** key fields at plan time, as ordinals, through
  `LookupTableSource.LookupContext.getKeys()`.
- The runtime hands over **plain values**: `CompletableFuture<Collection<RowData>> asyncLookup(RowData
  keyRow)`. There is nothing to translate, and nothing reads generated code.

So the work here is a rule, a node, and a runtime call of the shape the converter already emits:

- **`CosmosLookupJoinRule`** matches a join whose probe side is a Cosmos subtree and whose condition is
  a conjunction of equalities between a build-side field and a path Cosmos can address. It records the
  key ordinals and consumes the join.
- **`CosmosLookupJoin`** is a `ClrAsyncEnumerableRel` over the build side, holding the Cosmos subtree's
  statement. Its `Implement` emits one `Expression.Call`, which is what
  `CosmosToClrAsyncEnumerableConverter` already does.
- **The runtime** batches build rows, dedupes the keys, and fetches. Async throughout, which
  `ICosmosQueryExecutor` already is.

Two things this shape reaches that a rendered correlated predicate could not, and they are the reason
it is worth the weight:

- **Deduping.** The keys are data at that point, not a predicate, so a hundred build rows over ten
  distinct keys fetch ten.
- **The partition key.** If the key *is* the partition key, each batch routes to its partitions
  instead of fanning out; and where the key is `id` together with a complete partition key, this is
  `ReadManyItemsAsync` — a genuine batch point read, not a query at all. See *batch point reads*.

**Measured, and it is the measurement the feature turned on.** `WHERE c.category IN (@k0, …, @k99)` —
the form emitted, at the batch size emitted — reports `/category/?` under `UtilizedIndexes` with
nothing under `PotentialIndexes`. So the restriction is index-served and the lookup join is an
improvement rather than a scan with a hundred-term filter attached.

Still unmeasured: whether `ARRAY_CONTAINS(@keys, c.k)` is served the same way. It would take one
parameter for a variable-length batch instead of a hundred with padding, which is tidier but buys
nothing now that the padded form is known to work.

### Weakening a disjunction — *done*

Dropping a conjunct weakens; dropping a disjunct strengthens, so an `OR` with a branch Cosmos cannot
render was declined whole. `CosmosFilterSplitRule` now pushes `a OR w` where each `w` is implied by
its branch, and rechecks the original above it.

The weakening is "every path this branch reads is defined". **Measured**, and the measurement changed
the rule: the guess was that `NOT` flips the implication, and it does not — a comparison on an absent
property is undefined rather than false, and `NOT undefined` is not true either, so neither
`c.price > 5` nor `NOT (c.price > 5)` reaches a document without a `price`. What breaks the
implication is an operator that *observes absence*: `IS_DEFINED` returns a real boolean there, so
`NOT IS_DEFINED(p)` is genuinely true where the path is missing. The rest of the `IS_*` family is
refused with it, on the same reasoning rather than on measurement.

Had the polarity walk previously specified here been implemented, it would have been sound but
needlessly narrow — refusing every negated comparison.

Also settled on the way: `= null` is a comparison, not a definedness test. Undefined is not null.

Enumerated after the fact, and it found one: **SQL's own null tests observe absence** and are not in
the Cosmos family, because they are not Cosmos functions. `x IS NULL` renders as
`(NOT IS_DEFINED(x) OR IS_NULL(x))`, true where the path is missing, and the `IS_TRUE` family
collapses unknown to a definite boolean. Both are refused now, along with `IS DISTINCT FROM`. This
was a live hole in the first version of the rule — a branch using one can be true with the path
absent, so weakening it would have discarded exactly those rows.

### Smaller rules

- **Filter splitting above a projection** — *done.* The rule matched `Filter` over `TableScan` only, so a projection between the two cost the whole pushdown rather than the untranslatable half of it. It now matches any input and finds the container by walking, which is what `Split` already did to bind the fields.
- **Sorting above an aggregate** — *already handled, now covered.* Cosmos rejects `GROUP BY` and
  `ORDER BY` together, and it turns out the rule never could produce the combination:
  `TryBindOutput` has no `Aggregate` case, so a sort over a pushed aggregate fails to bind and
  `CosmosSortRule` declines. A guard added to the rule for this was dead code and was dropped —
  probed by removing it, and the behaviour did not change. The aggregate pushes and the sort
  stays in Calcite, which is the better plan anyway: it sorts one row per group rather than the
  container. There is now a test saying so, which there was not.
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

### Cosmos functions as schema functions — *closed, and deliberately not done*

The idea was that registering `IS_DEFINED`, `FULLTEXTCONTAINS` and the rest as schema functions would
save a caller from chaining `CosmosOperators.Instance` into the validator's operator table.

**Measured, and it would make things worse.** A Cosmos function that cannot be pushed — applied to a
computed projection, say, which has no document path for a statement to name — currently fails
*while planning*, because nothing can render it and nothing can evaluate it either. A schema function
is bound to a CLR method, which gives Calcite something to call: the failure would move from plan
time to the middle of an enumeration, with rows already flowing.

An operator table is also Calcite's own answer for dialect-specific functions — `SqlLibrary` and
`SqlLibraryOperatorTableFactory` — so the current shape matches the framework rather than working
around it. The ergonomic wart is real but has no clean fix: `SqlLibrary` is a closed enum, so there
is no `fun=cosmos` route for an out-of-tree adapter to register itself under.

Covered by `CosmosFunctionResolutionTests`, which pins both halves: the functions resolve only with
the table chained, and one that cannot be pushed fails while planning.


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
  database, which is how both Cosmos and Calcite nest. Container *definitions* are still read eagerly
  when the schema is built — small, and Calcite asks for the whole map at once — so an account with
  many databases pays a read per container to reach one. Statistics are no longer part of that cost.
  Lazy subschemas would close the rest and want a lazy `Map`.
- **Client disposal** — *small.* The schema owns a client for the life of the process because Calcite
  offers no disposal hook. Worth revisiting against `SchemaPlus` rather than left as a comment.
- **Server-side functions** — *medium.* Cosmos has stored procedures and JavaScript UDFs. A UDF is
  nameable in a query, so it could be exposed as a Calcite operator the way the built-ins are.

---

## 8. Observability

- **A diagnostics surface** — *done.* `CosmosInstrumentation` publishes a `Meter` and an
  `ActivitySource`, both named `Apache.Calcite.Cosmos.Adapter`. Calcite was ruled out rather than
  passed over: every `Hook` value is plan-time, and no adapter in the tree reports execution
  statistics through one. `CosmosDiagnostics` is the piece still not surfaced — it is a large JSON
  blob per response, so it wants a switch of its own rather than to ride on the span.
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
- **Emulator gaps, recorded** — *small.* The emulator silently discards composite indexes, does not
  implement full text search, reports a flat 1 RU for every request, and returns no index metrics.
  All four are known and all four are why the corresponding tests report inconclusive there rather
  than failing; none is asserted, so a future emulator that fixes one would go unnoticed.

---

## 10. Unsettled questions

These are not features. They are things believed but not measured, and each one is a defect waiting
for the right query.

- **The composite index requirement** — *settled.* Measured on a real account against a container
  with no composite index: `ORDER BY c.category, c.price` is rejected with 400 and `ORDER BY c.price`
  over the same container is served. The guard is refusing exactly what the service refuses, and
  costs no pushdown that was ever available.
- **`DISTINCT` with `ORDER BY`.** Assumed incompatible, never tested.
- **Two-argument `TRIM` and `TRUNCATE`.** Left out for want of a measurement.
- **Out-of-domain arithmetic.** Measured: `ASIN(2)`, `ACOS(2)`, `SQRT(-1)` and `LOG(0)` each fail the
  whole query where Calcite yields NaN. Pushed anyway, consistently, and recorded so the consistency
  is a decision.

---

## 11. Read off Flink's connector SPI

Flink is the most complete Calcite-based connector framework in the open, and its source and sink
*ability* interfaces are a catalogue of what a pushdown-capable connector can offer. Each one below is
an interface a Flink connector implements; what follows is what it would mean for Cosmos. Read as a
checklist rather than a plan — several are already done, several do not apply, and the ones that do
are marked.

Every Cosmos operation named below was compile-checked against the SDK this project references, so
the mappings point at something that exists rather than at something that sounds right.

### Source abilities

| Flink | Here |
|---|---|
| `SupportsFilterPushDown` | **done** — and note the API shape: it returns *accepted* and *remaining* filters, so partial pushdown is a first-class contract rather than an afterthought. `CosmosFilterSplitRule` reaches the same place by rule. |
| `SupportsProjectionPushDown` | **done**, including nested paths, which Cosmos addresses natively. |
| `SupportsLimitPushDown` | **done** — `OFFSET`/`LIMIT`, plus `MaxItemCount` as the page size. |
| `SupportsAggregatePushDown` | **partly done.** Flink pushes the *local* aggregate and combines above it. Worth taking: a `GROUP BY` that cannot be pushed whole may still be pushable as a partial aggregate the plan finishes — see below. |
| `SupportsStatisticReport` | **done** — this is FLIP-231, already the model for reading statistics lazily at optimisation. |
| `SupportsPartitionPushDown` | **worth taking.** Hands the planner the list of partitions. `GetFeedRangesAsync` gives the physical ones; see *parallel scan by feed range*. |
| `SupportsDynamicFiltering` | **worth taking** — FLIP-248 dynamic partition pruning. The other half of sideways information passing: instead of fetching by key, the build side's values *prune partitions* on the probe side at run time. Complements the lookup join rather than competing with it, and for Cosmos the unit pruned is a physical partition. |
| `SupportsLookupCustomShuffle` | **worth taking, and it is the big one.** A connector says how rows should be partitioned before they reach the lookup. Shuffling build rows by Cosmos partition key would make every batch single-partition, turning a fan-out into one request — which is the saving the lookup join otherwise leaves on the table. |
| `SupportsReadingMetadata` | **small.** Metadata columns declared rather than always promoted: `_rid`, `_self`, `_attachments`, and the per-item `ttl`. Would also let `_ts`/`_etag` stop occupying ordinary column ordinals. |
| `SupportsRowLevelModificationScan` | **relevant once there is DML.** The scan is told it is feeding an `UPDATE`/`DELETE`, so it can read only what the modification needs — for Cosmos, `id` and the partition key rather than whole documents. |
| `SupportsWatermarkPushDown`, `SupportsSourceWatermark` | **only with the change feed.** Streaming concepts; the change feed is the analogue, and `_ts` the natural watermark. See *change feed*. |

### Lookup abilities

| Flink | Here |
|---|---|
| `AsyncLookupFunctionProvider` | **in progress** — this is what `CosmosLookup` is. Async is the only option here, which matches. |
| `PartialCachingLookupProvider` | **done, for one execution.** A bounded cache in front of the lookup; absence is remembered too, which is the case it most needs to hold. Scoped to a single execution deliberately: it then answers for no staleness the join did not already have. A cache *across* executions is the open part, and it is the part that needs a TTL and an owner. |
| `FullCachingLookupProvider` | **worth considering** for small containers: load the whole thing once and never call the service on a miss, with a reload strategy. A lookup table of a few thousand documents is exactly this. |
| `LookupOptions` | The options that go with the above — cache type, maximum rows, TTL, reload strategy. Worth copying the *names* so anyone who knows Flink knows these. |
| Lookup retry (FLIP-234) | **probably not.** Flink retries a lookup that comes back empty, for late-arriving reference data. The SDK already retries throttling, which is the failure that actually happens here. |

### Sink abilities, for when there is DML

All of these bear on *Writing* above, and several map onto a Cosmos operation that is cheaper than the
obvious one:

| Flink | Here |
|---|---|
| `SupportsTargetColumnWriting` | **Patch.** Writing named columns only is exactly `PatchItemStreamAsync`, which sends the changed properties rather than the whole document. |
| `SupportsRowLevelUpdate` | Same — an `UPDATE` touching three fields should be a patch, not a replace. |
| `SupportsRowLevelDelete` | Delete by `id` and partition key, which is a point operation. |
| `SupportsDeletePushDown` | `DELETE … WHERE` decomposed and pushed. Where the predicate pins a partition key and nothing else, the service has `DeleteAllItemsByPartitionKeyStreamAsync` — a whole-partition delete that is not a query at all. |
| `SupportsTruncate` | `TRUNCATE TABLE` — per-partition deletes, or recreating the container, which is cheaper and has different semantics. Worth deciding deliberately rather than by default. |
| `SupportsOverwrite` | Upsert, which is native (`UpsertItemStreamAsync`). |
| `SupportsPartitioning` | Writes routed by partition key. Bulk mode already groups by partition, so this is mostly about telling the planner. |
| `SupportsWritingMetadata` | Writing the per-item `ttl`, which is a real Cosmos feature with no column to put it in today. |
| `SupportsStaging`, `SupportsBucketing` | **Not applicable.** Two-phase commit for `CTAS`, and bucketed file layouts; neither has a Cosmos counterpart. |

### Found while building the lookup join

- **A caller planning joins must run the calc rules as a pass after the planner** — *settled, and
  worth a line in the README.* `ClrAsyncEnumerableProject` throws when implemented, which is
  upstream's arrangement rather than a gap: `EnumerableProject.implement()` throws too, saying
  *"EnumerableCalcRel is always better"*, and `EnumerableProjectRule` creates a `Project` there as
  here. It is also the *cheaper* node — `Calc`'s inherited cost counts a unit per expression and
  `Project`'s does not — so handed to Volcano the two compete and the throwing one wins. Measured:
  adding `ClrAsyncEnumerableRules.CalcRules()` to the planner does not help for exactly that reason.
  Run afterwards, as Calcite's `Programs.CALC_PROGRAM` does, there is no competition and every
  surviving projection becomes a calc. Invisible until there is a join, because until then every
  projection is pushed into the container and none survives to be implemented.
- **A row builder needs the shape the statement projects.** The lookup join'''s execution tests first
  failed with a null reference inside the join'''s result selector, because the stub returned raw
  documents rather than the projected object the statement asks for. Real responses carry the
  projection, so this is a fixture concern rather than a defect — but which access produced the null
  was not established, and it is worth knowing whether a malformed response fails loudly or quietly.

### The three worth doing first

1. **Shuffle the build side by partition key** before the lookup (`SupportsLookupCustomShuffle`). It
   turns each batch from a fan-out into a single-partition request, and it is the difference between
   the lookup join being an improvement and being a large one. Wants measuring before designing:
   routing per key means one request per distinct key, and whether that beats one cross-partition
   query depends on the key count and the partition count. Grouping keys by *feed range* — one
   request per physical partition — is the likelier shape.
2. **Patch for `UPDATE`** (`SupportsTargetColumnWriting`). When DML arrives, sending three properties
   instead of a whole document is the difference in RU, not a refinement of it.
3. **A cache across executions** (`FullCachingLookupProvider`). The within-execution one is done; the
   one that survives a query needs a TTL, a size bound shared between queries, and something that
   owns it — the schema is the obvious candidate and the wrong one if two connections disagree about
   freshness.
