# Outstanding work

What a complete adapter would have, sized and reasoned, so that the next session picks up an argument
rather than a list.

**Sizes.** *Small* is a translator case and a test. *Medium* is a node, a rule, or an SDK surface.
*Large* needs a design decision recorded in `DESIGN.md` before any code.

**On finishing.** When an item is done, remove it — the entry, its rationale, and any *done* marker
elsewhere in this file. This file holds only work still to be done; what was decided belongs in
`DESIGN.md`, what was built is visible in the code and its tests, and history lives in git. A *done*
paragraph kept here is a second copy of one of those, aging independently.

**On testing a change.** Before believing a test covers what it claims, check that it *fails without
the change*. This has repeatedly told both stories: fixes whose tests genuinely depended on them, and
guards that turned out to be dead code — the case they guarded already unreachable. Neither is
visible from a green suite.

**On evidence.** Where a claim about the service is unverified it says so. The emulator has disagreed
with Azure in both directions — accepting an `ORDER BY` over an unnest alias that Azure rejects, and
rejecting the full text search Azure runs — so "the reference says" is not a measurement. Point
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` at a real account and the suite runs against one.

---

## 0. Resuming

**507 tests: 501 passing, 6 skipped**, on net8.0 and net10.0, against Apache.Calcite 2.0.0-pre.3.
The skips are things only a real account can answer; the suite runs against one when
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` name it, and reports inconclusive rather than passing
where the emulator cannot. Several facts in this file and in `DESIGN.md` were settled that way —
most recently the lookup-routing measurement — each time with an Azure account, used and deleted.

No PRs are open; `main` is where the work is and a new branch starts from it. **One decision is in
flight rather than any code:** declared columns — a `columns` operand promoting caller-declared,
typed document paths to real columns — is built and parked on the `declared-columns-parked` branch,
awaiting the owner's call on whether the adapter should have it and in that shape. See *Where to
start*.

Reading, writing (`INSERT`, `DELETE`, and `UPDATE` of the map column as a whole-document replace),
the lookup join, partial aggregates, and the diagnostics surface are complete and covered. What
remains below is not started.

### Running the sample

```
dotnet run --project samples/Apache.Cosmos.Sample/Apache.Cosmos.Sample.csproj
```

It needs the Cosmos emulator on `localhost:8081` and prints the docker command if it is missing. It
seeds both sources and is safe to re-run. What it demonstrates is the lookup join across two adapters:
the CSV side's three product ids are pushed into Cosmos, so the container is filtered at the service
rather than read whole.

### Integration requirements, recorded in the README

Two things trip a host and fail with messages that name nothing useful: the calc rules must run as a
*pass* after the planner (`Programs.CALC_PROGRAM`'s shape — given to Volcano they do nothing), and a
model must name `CosmosSchemaFactory` assembly-qualified with the assembly already loaded. The README
carries both with the reasoning.

### Where to start

1. **The declared-columns decision** (section 6) — not work but a call to make, and three items
   queue behind it: the `UPDATE` patch tier (section 3), the nullable-aggregate rewrite (section 5),
   and a temporal basis (section 4). The implementation is parked on `declared-columns-parked`; the
   design is in `DESIGN.md` under *Declared columns* on that branch.
2. **Adopting Calcite's metadata system** (section 1) — the owner's direction, and an audit agrees
   it is overdue: `CosmosFilter.computeSelfCost` hand-rolls a partition-count discount that is
   `Distribution`/`Parallelism` metadata by another name, `getStatistic` reports no distribution
   though a container is hash-distributed by its partition key, and the document-size costing item
   is a `Size` (`averageRowSize`) provider rather than another multiplier. One provider also
   carries the statistics-after-pushdown item and the whole-partition `DELETE` capability gate.
   First act: the Janino-under-IKVM feasibility probe recorded in `DESIGN.md` under *Deleting a
   whole partition*.
3. **Whole-partition `DELETE`, probe-gated** (section 3) — the design is settled in `DESIGN.md`;
   the work is the capability metadata, the rule consulting it, and the count-then-delete
   execution, together, once an enrolled account exists to verify against.
3. **The small-coverage batch** — the scalar functions still to map (section 4), `SELECT DISTINCT`,
   native `IN`/`BETWEEN` with its pricing measurement, `TOP`, and the remaining emulator gaps
   asserted (section 9) — each a translator case and a test, and each a differential corpus entry
   as it lands.

---

## 1. Statistics and the cost model

`getStatistic` reports keys from declared metadata and a row count read from the service; the cost
model is still inference over constants. Everything here is a read of something the service already
knows, or the model those reads deserve.

### Document size into the cost model — *small*

Average document size is already derived from the container's resource usage and not yet used. It is
what a row costs to move, which for a map row model carrying whole documents dominates.

### Provisioned throughput — *small*

`ReadThroughputAsync`. Not needed to compare two plans, but it is the denominator that turns an RU
estimate into a latency estimate, and it distinguishes a container that can absorb a scan from one
that cannot.

### Feeding `RequestCharge` back — *large*

The measured charge is on the `cosmos.request_charge` histogram and the `cosmos.query` span. A
measured charge for a query shape is worth more than the estimate that was used to choose it, but a
cost model that learns needs somewhere to keep what it learnt — the statistics-refresh question
below wearing a different hat.

### Per-partition skew — *not available; recorded so nobody looks again*

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

Queries execute through `GetItemQueryStreamIterator`, with a pinned `id` and complete partition key
recovered as a point read. The SDK's other cheap routes are unused.

### The sample against SQL Server — *small*

Apache.Calcite 2.0.0-pre.3 fixed the ADO.NET adapter against SQL Server
([calcite-dotnet#24](https://github.com/ikvmnet/calcite-dotnet/issues/24)), so the sample's CSV side
can become the SQL Server it was meant to be — a one-line change, plus the SQL Server the sample
would then need running beside the emulator, which is the actual decision. Nothing in CI runs the
sample either way; it was last verified by hand against pre.3.

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

Writes are item CRUD behind a `TableModify` — Cosmos SQL has no DML, and does not need to for the
adapter to write. What each statement does and refuses is recorded in `DESIGN.md` under *Writing*.

### `UPDATE`, the patch tier — *blocked on the declared-columns decision*

`SET "_MAP" = …` executes as a whole-document replace. What remains is the cheap tier: a targeted
`SET` of a plain document property as `PatchItemAsync`, sending changed properties rather than the
document. Its targets are declared columns — parked pending the decision in *Where to start* — and
the tier is one rule-and-writer step once that lands. The execution ladder above it (static
decomposition via a mutation operator, the diff and blind-patch optimizations) is recorded in
`DESIGN.md` under *Updating*.

### Whole-partition `DELETE` — *medium, designed; gated on a probed capability*

A predicate pinning exactly the complete partition key becomes
`DeleteAllItemsByPartitionKeyStreamAsync` — one request, no query at all — where the account
supports it, and stays the scan-and-delete it is today where it does not. The design is recorded in
`DESIGN.md` under *Deleting a whole partition*: the operation is an account-level public preview
(measured: the emulator answers 400 and deletes nothing; portal-only enrollment, not registrable
from the CLI under the test subscription), so **the rule consults a probed capability** — the
operation invoked against a random key value, safe whichever answer comes back, cached on the
container metadata behind a lazy provider the way statistics are. The count comes from a `COUNT(*)`
first; visibility-on-return is documented as immediate but *unmeasured*, and
`WholePartitionDeleteProbe` asserts the block on measuring it — an environment that implements the
operation fails that test loudly, which is the signal to verify the claim and finish this item.

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
- **Currently declined, admissible with work** — `SUBSTRING` without a length (`LENGTH(s)` supplies
  it); `LIKE` with `ESCAPE`, and a bracket-escaping rewrite that would lift the bracket-pattern
  decline (Cosmos `LIKE` reads `[…]` as a character range where SQL does not — measured, and why
  bracket and computed patterns are refused); `TRIM` of a non-space character and `TRUNCATE` to
  decimal places, both needing Cosmos's two-argument arity **verified** first; `IS TRUE`/`IS FALSE`/
  `IS DISTINCT FROM`, expressible with the `??` operator once the null-versus-undefined semantics are
  measured.

### Temporal — *large, and its prerequisite is the declared-columns decision*

Cosmos has `DateTimeAdd`, `DateTimeDiff`, `DateTimePart`, `DateTimeBin` and tick conversions; Calcite
has `EXTRACT`, `TIMESTAMPADD`, `TIMESTAMPDIFF`. The mapping is mechanical and the representation is
not: a date is an ISO string or an epoch number by application convention, and `_ts` is the only value
whose encoding the service defines. Pushing a temporal function down means declaring what the column
*is* — which is what a declared column would state.

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

### Nullable aggregates — *blocked on the declared-columns decision*

The null-semantics refusals are the biggest source of declined aggregates: `SUM(c.v)` over a nullable
column is `undefined` at the service where SQL skips the null. The fix is rewriting the rendered
argument so Cosmos skips it too — aggregates skip *undefined*, and arithmetic on a JSON null yields
it, so `SUM(c.v * 1)` is the candidate for a column declared numeric. The rewrite is type-directed
(`* 1` over a string silently drops it from `MIN`/`MAX`), and only declared columns give it a type to
fire on. Measure on the emulator before building: that the null is skipped, that an all-null group
comes back as SQL's null does, and that `* 1` does not disturb a large integer.

### Smaller rules

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

- **Declared columns — *built, and parked pending the owner's decision*.** A caller-declared, typed
  document path promoted to a real column, via a `columns` operand — trusted the way a partition
  key path is, never inferred. Three items converge on it: the `UPDATE` patch tier (section 3), the
  nullable-aggregate rewrite (section 5), and a temporal basis (section 4). The implementation —
  operand, metadata, row typing, the `CAST`-folding the typed columns forced, and tests — sits on
  the `declared-columns-parked` branch, held there deliberately: it is a new public surface, and
  whether the adapter should have it, and in this shape, is the owner's call. Take the branch, ask
  for a different shape, or drop it; nothing else depends on it yet.
- **Binary** — *small.* `BINARY`/`VARBINARY` read base64 from a JSON string. Unverified against the
  service, because nothing in the test data is binary.
- **Temporal representation** — see *Temporal* above. The reading side handles ISO strings and epoch
  numbers; what is missing is any declared basis for deciding which a column holds.

---

## 7. Provider and integration

- **Schema functions** — *medium, and the trade is the decision.* `CosmosOperators.Instance` is a
  `SqlOperatorTable` a caller must chain, which is why full text is unreachable from a bare
  `CalciteConnection`. Registering the functions on the schema instead means `ScalarFunctionImpl`
  over real CLR methods, and these functions cannot execute outside Cosmos, so those methods would
  throw: a non-pushed query becomes a run-time failure mid-enumeration rather than a plan-time
  refusal. That downside is measured and pinned by `CosmosFunctionResolutionTests`; there is also no
  `fun=cosmos` route, `SqlLibrary` being a closed enum.
- **Connection options as operands** — *small.* Consistency level, preferred regions, application name,
  for callers who do not want to write a factory.
- **Lazy subschemas** — *small.* Container *definitions* are read eagerly when an account-level schema
  is built, so an account with many databases pays a read per container to reach one. Statistics are
  already lazy; the definitions want a lazy `Map`.
- **Client disposal** — *small.* The schema owns a client for the life of the process because Calcite
  offers no disposal hook. Worth revisiting against `SchemaPlus` rather than left as a comment.
- **Server-side functions** — *medium.* Cosmos has stored procedures and JavaScript UDFs. A UDF is
  nameable in a query, so it could be exposed as a Calcite operator the way the built-ins are.

---

## 8. Observability

- **`CosmosDiagnostics`** — *small.* The one signal not surfaced: a large JSON blob per response, so
  it wants a switch of its own rather than to ride on the `cosmos.query` span.
- **RU regression tracking** — *medium.* The charge is on the histogram; assert that a query shape
  does not get more expensive.

---

## 9. Testing

- **A real account in CI** — *medium.* The emulator accepts statements Azure rejects and rejects
  features Azure implements; both have been found by hand. A nightly job against a real account is
  what stops the next one being found by a user — and it is where `CosmosDifferentialTests` and the
  routing measurement rerun their evidence against the real service.
- **Growing the differential corpus** — *small, forever.* The harness is done (`DESIGN.md` under
  *Differential testing*); every new pushdown should bring its statements to the corpus, and every
  translator addition is a candidate. Probed and in: filters, sorts, the aggregate forms, `LIKE`,
  and the array traversal — the guess that the oracle could not evaluate an in-process unnest was
  wrong, and the corpus says so.
- **Emulator gaps, asserted** — *small.* The emulator silently discards composite indexes, does not
  implement full text search, reports a flat 1 RU for every request, and returns no index metrics.
  All four are known and are why the corresponding tests report inconclusive there rather than
  failing; none is *asserted*, so a future emulator that fixes one would go unnoticed.
- **A malformed response's failure mode** — *small.* A lookup-join stub returning raw documents
  instead of the statement's projection once produced a null reference inside the join's result
  selector, and which access produced it was never established. Worth knowing whether a malformed
  service response fails loudly or quietly.

---

## 10. Unsettled questions

These are not features. They are things believed but not measured, and each one is a defect waiting
for the right query.

- **`DISTINCT` with `ORDER BY`.** Assumed incompatible, never tested.
- **Two-argument `TRIM` and `TRUNCATE`.** Left out for want of a measurement.

---

## 11. Read off Flink's connector SPI

Flink is the most complete Calcite-based connector framework in the open, and its source and sink
*ability* interfaces are a catalogue of what a pushdown-capable connector can offer. Each row below is
an interface a Flink connector implements and what it would mean for Cosmos; abilities already
covered here are not listed. Every Cosmos operation named was compile-checked against the SDK this
project references.

### Source abilities

| Flink | Here |
|---|---|
| `SupportsPartitionPushDown` | **worth taking.** Hands the planner the list of partitions. `GetFeedRangesAsync` gives the physical ones. |
| `SupportsDynamicFiltering`, `SupportsLookupCustomShuffle` | **closed by measurement** — the service's query router already prunes an `IN` over the partition key to the partitions owning the values, cross-partition execution already fans out per feed range, and per-key routing costs the per-query floor times the key count. See *The lookup restriction is already routed* in `DESIGN.md`; `CosmosLookupRoutingMeasurementTests` reruns the evidence against any real account. |
| `SupportsReadingMetadata` | **small.** Metadata columns declared rather than always promoted: `_rid`, `_self`, `_attachments`, and the per-item `ttl`. Would also let `_ts`/`_etag` stop occupying ordinary column ordinals. |
| `SupportsRowLevelModificationScan` | **worth taking.** The scan is told it is feeding an `UPDATE`/`DELETE`, so it can read only what the modification needs. Both are implemented and read whole documents to use two paths out of them — `id` and the partition key — which for a map row model is the whole cost of the statement. |
| `SupportsWatermarkPushDown`, `SupportsSourceWatermark` | **only with the change feed.** Streaming concepts; the change feed is the analogue, and `_ts` the natural watermark. See *change feed*. |

### Lookup abilities

| Flink | Here |
|---|---|
| `FullCachingLookupProvider` | **worth considering** for small containers: load the whole thing once and never call the service on a miss, with a reload strategy. A lookup table of a few thousand documents is exactly this. The partial cache — per execution and, by declared policy, across them — is done; see `DESIGN.md` under *The lookup join's caches*. |
| Lookup retry (FLIP-234) | **probably not.** Flink retries a lookup that comes back empty, for late-arriving reference data. The SDK already retries throttling, which is the failure that actually happens here. |

### Sink abilities

| Flink | Here |
|---|---|
| `SupportsTargetColumnWriting`, `SupportsRowLevelUpdate` | **Patch** — the `UPDATE` tier waiting on declared columns; see section 3. |
| `SupportsDeletePushDown` | The whole-partition delete; see section 3. |
| `SupportsTruncate` | `TRUNCATE TABLE` — per-partition deletes, or recreating the container, which is cheaper and has different semantics. Worth deciding deliberately rather than by default. |
| `SupportsOverwrite` | Upsert, which is native (`UpsertItemStreamAsync`). |
| `SupportsPartitioning` | Writes routed by partition key. Bulk mode already groups by partition, so this is mostly about telling the planner. |
| `SupportsWritingMetadata` | Writing the per-item `ttl`, which is a real Cosmos feature with no column to put it in today. |
| `SupportsStaging`, `SupportsBucketing` | **Not applicable** — two-phase commit for `CTAS` and bucketed layouts have no Cosmos counterpart; recorded so nobody looks again. |
