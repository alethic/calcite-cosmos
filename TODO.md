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
the change*. This caught two things in one session: a filter-split test that genuinely depended on
its fix, and a guard added to `CosmosSortRule` that turned out to be dead code — the case it guarded
was already unreachable, and removing it changed nothing. Same probe, opposite conclusions, and
neither was visible from a green suite.

**On evidence.** Where a claim about the service is unverified it says so. The emulator has disagreed
with Azure in both directions — accepting an `ORDER BY` over an unnest alias that Azure rejects, and
rejecting the full text search Azure runs — so "the reference says" is not a measurement. Point
`COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` at a real account and the suite runs against one.

---

## 0. Resuming

**466 tests: 461 passing, 5 skipped**, on net8.0 and net10.0. The skips are things only a real account
can answer; the suite runs against one when `COSMOS_TEST_ENDPOINT` and `COSMOS_TEST_KEY` name it, and
reports inconclusive rather than passing where the emulator cannot. Several facts in this file and in
`DESIGN.md` were settled that way — an Azure account, used and deleted.

Nothing is in flight: PRs #1 through #3, #5 (the DML work), #6 (any container on a lookup join's
probe side) and #8 (dependencies) have all merged, so `main` is where the work is and a new branch
starts from it. The repository moved to the `ikvmnet` organization.

**Nothing is half-finished.** The lookup join, the disjunction weakening, the diagnostics surface, the
statistics work, the Entra support and now `INSERT` and `DELETE` are complete and covered. What
remains below is not started.

**The dependencies are current**, as of [PR #8](https://github.com/ikvmnet/calcite-cosmos/pull/8):
Apache.Calcite at 2.0.0-pre.2, MSTest.Sdk at 4, Azure.Identity at 1.21.0, Spectre.Console at 0.57.2,
and the `EXPLAIN` rework the first of those allowed. Green on both frameworks across all five
platforms, so the parts worth doubting are settled — it compiles, and the suite runs.

Two of those carried a change that is not a version number, and both are worth knowing before
touching them again:

- **MSTest.Sdk 4 stopped referencing `Microsoft.NET.Test.Sdk` for every test application**, doing it
  only under `UseVSTest`. The workflow hands `dotnet test` a built assembly with the trx logger and a
  filter, which is the VSTest command line, so the test project names that reference itself. Removing
  it means rewriting how CI invokes tests.
- **Azure.Identity 1.21.0 is a facade.** Every type moved to `Azure.Core` behind `TypeForwardedTo`,
  which is why the package is 18KB with no code of its own. Nothing here had to change, but a reader
  wondering where `DefaultAzureCredential` went now knows.

**What no build has checked is the sample**, which nothing in CI runs. `EXPLAIN PLAN FOR` on the
asynchronous path should now print the physical tree, `CosmosLookupJoin` and all — but `Explain`
returns null on any exception, so a failure there is silent and shows as a missing plan panel rather
than as anything louder. Run it against the emulator and look for the tree before believing it.

### Running the sample

```
dotnet run --project samples/Apache.Cosmos.Sample/Apache.Cosmos.Sample.csproj
```

It needs the Cosmos emulator on `localhost:8081` and prints the docker command if it is missing. It
seeds both sources and is safe to re-run. What it demonstrates is the lookup join across two adapters:
the CSV side's three product ids are pushed into Cosmos, so the container is filtered at the service
rather than read whole.

### One thing still waiting on `ikvmnet/calcite-dotnet`

- **[#24](https://github.com/ikvmnet/calcite-dotnet/issues/24) — the ADO.NET adapter cannot read SQL
  Server.** Its metadata read calls `GetInt32` on columns SQL Server types as `tinyint`, so every
  query against every table throws. The sample uses the CSV adapter instead; SQL Server is the better
  demonstration and switching back is a one-line change.

**`EXPLAIN` over an async-only table is fixed**, by
[PR #27](https://github.com/ikvmnet/calcite-dotnet/pull/27), released as Apache.Calcite
2.0.0-pre.2 and taken here. Both halves of the corner closed at once: `ClrExplainBindable` binds on
either path, so the asynchronous read no longer refuses a result that is plan text rather than an
asynchronous node; and the two Clr conventions gained a converter each way, so the explained query
planned into `ClrEnumerableConvention` converts instead of failing with *"not enough rules to produce
a node with desired properties"*. The sample now asks for `EXPLAIN PLAN FOR` on the asynchronous path
and prints the physical tree — `CosmosLookupJoin` and all — and the `WITHOUT IMPLEMENTATION` fallback
is gone. Not run: see the note under *Resuming*.

### The one integration requirement this adapter adds

**A host must run the calc rules as a pass after the planner**, and it is easy to miss because the
failure names nothing useful — a plan that cannot be implemented, with no indication of what is
missing:

```csharp
var program = new HepProgramBuilder();
foreach (var rule in ClrAsyncEnumerableRules.CalcRules())
    program.addRuleInstance(rule);
```

It is Calcite's `Programs.CALC_PROGRAM` and it is a *pass*, not rules for the planner — given to
Volcano it does nothing, because `ClrAsyncEnumerableProject` is the cheaper node and also the one that
throws when implemented. Without it, a projection above a join has nothing to implement it. It does
not arise without a join, because every other projection is pushed into the container.

**And a model names a factory assembly-qualified** — `…CosmosSchemaFactory, Apache.Calcite.Cosmos.Adapter`.
The bare name resolves to nothing through IKVM, and the assembly must already be loaded when the model
is read. Both are in the README now; the example there was wrong for as long as it existed, because
nothing exercised the model path.

### Where to start

Everything small is done. Each of these is larger, and each has a stated prerequisite rather than an
open question:

1. **Declared columns** (section 6) — the keystone, found while sizing `UPDATE`: every `SET` target
   the current row model can name is unpatchable, so `UPDATE` as a patch (section 3), the
   nullable-aggregate rewrite (section 5) and a temporal basis (section 4) all wait on typed,
   caller-declared columns existing at all. Design first — the questions are listed in section 6 —
   then the promotion is mechanical.
2. **Shuffle the build side by partition key** (section 11) — the biggest remaining win for the lookup
   join, but measure first: routing per key means one request per distinct key, and whether that beats
   one cross-partition query depends on key count against partition count. Grouping by feed range is
   the likelier shape.
3. **A cache across executions** (section 11) — the within-execution one is done; this one needs a TTL,
   a bound shared between queries, and something that owns it.

**That one thing is now fixed, and the experiment this file proposed was the wrong one.** A join
with the other container on the probe side passes even with the binding in place: the instance a
planner consults is not the one `addRule` accepted but the freshest at the rule's *first* firing,
which then stays bound for the run — and since `CosmosConvention.register` rebuilds the rule set
per convention and a join's inputs register left before right, a lone join's freshest instance is
its own probe side's. Two containers passed in either orientation by registration order. A join of
*three* containers showed it: the instance bound at the first join declined the second's container,
which was read whole. Fixed the way the write rule was — no binding, the container read from the
probe side — and guarded by `EveryContainerInAQueryCanBeOnTheProbeSide`, whose remarks record both
the mechanism and why the two-container experiment could not have caught it.

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
`INSERT`, `UPDATE` or `DELETE`, but the SDK has item CRUD, and Calcite expresses writes as a
`TableModify` consuming rows rather than as generated SQL.

`INSERT` and `DELETE` are done. `UPDATE` is not, and is the interesting one left.

### `INSERT` — *done*

`CosmosTableModify` over `CreateItemStreamAsync`. What it writes is recorded in `DESIGN.md` under
*What an insert writes*: the map column is the document, a promoted column sets the property it
projects, and a promoted column contributes only when it is not null — forced, because an unmentioned
column arrives as null and the alternative overwrites the document it was handed with a row of nulls.

`ModifiableTable` turned out **not to be needed**: `SqlToRelConverter` falls back to
`LogicalTableModify` where a table unwraps to none, so a rule matching that is the whole entry point.
Which is as well, since `getModifiableCollection` would have meant a collection blocking on
`CreateItemAsync` per element — the sync-over-async pull the asynchronous convention exists to exclude.

Four things were measured rather than assumed, and three of them changed the design:

- **`INSERT` does not reach a rule without column strategies.** `_MAP` and `id` are both `NOT NULL`
  and both true, so the validator refuses an insert omitting either. `ColumnStrategy` separates *not
  null in the table* from *optional in an insert*, which is the distinction actually wanted.
- **`STORED`, not `VIRTUAL`, for `_ts` and `_etag`.** Both refuse a write; `VIRTUAL` also means *not
  stored*, and makes Calcite drop the column from the scan and project a null. Forty tests failed at
  once. The row type is identical either way.
- **The write rule cannot be bound to a convention.** A converter rule's description comes from the
  traits it converts between, and `NONE → CLR_ASYNC_ENUMERABLE` names no container, so per-container
  instances collide and a planner keeps one. With two containers registered, an insert into the first
  stopped planning.
- **A map literal cannot be inserted** — Calcite cannot build a type spec for `MAP<VARCHAR, ANY>`, so
  implicit coercion throws. A source column already of that type is fine, which is what a scan of
  another container gives, so copying documents between containers is unaffected.

### `DELETE` — *done*

`DeleteItemStreamAsync` given `id` and the partition key, both read out of the document the row
describes — which is also how a nested partition key path works, that not being a promoted column.

**Read-then-delete is allowed rather than refused**, reversing what this file previously guessed. The
scan feeding the modify is visible in the plan, and refusing would make `DELETE … WHERE price > 100`
impossible rather than expensive. Where the predicate pins `id` and a complete partition key the scan
is already a point read.

Still open: the whole-partition case. A predicate pinning only the partition key could be
`DeleteAllItemsByPartitionKeyStreamAsync`, which is not a query at all — `SupportsDeletePushDown` in
section 11.

### `UPDATE` — *large, and blocked on declared columns*

Cosmos has patch operations (`PatchItemAsync`) that map onto a targeted `SET`, and the planner
already hands over what a patch needs — `updateColumnList` and `sourceExpressionList`, named columns
and their new values, separately from the rows.

**Sized up, and the question this entry used to pose answered itself by enumeration** — recorded in
`DESIGN.md` under *Updating*. A `SET` target is a table column, and every column the row model has
is unpatchable: `_MAP` is the whole document, which a patch has no operation for; `id` is identity
and the partition key is placement, both of which the service forbids patching; `_ts` and `_etag`
are validator-refused already. The example plan this entry showed — `SET category` — sets the
partition key. So there is no translation to write until the row model has plain document columns,
which is *declared columns* (section 6), and `UPDATE` stays declined outright until then — the right
answer for a statement the adapter cannot carry out. The null-write and no-`If-Match` decisions are
recorded in `DESIGN.md` so the eventual implementation inherits them.

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

### Half a join, as a lookup join — *done*

A relational join is not expressible in Cosmos, so both sides used to be read whole and joined in
process. `CosmosLookupJoin` makes one side pay for the other: the build side's keys are collected,
deduplicated, and sent with the statement, so only documents that could match come back.

Flink's lookup join is the shape, and the important part is what it does *not* do. The correlation
variable Calcite uses to express "a value that will exist later" is a planner-level device — Flink's
rule consumes it and the physical node never sees one, its `decorrelate()` rewriting every reference
as an ordinary field reference. Nothing correlated reaches the runtime; what crosses is key values,
which is what `asyncLookup(RowData)` receives there. No in-tree storage adapter renders a correlation
variable, and the only one that touches `RexCorrelVariable` is JDBC, which expresses correlation
natively in SQL.

Inner joins on a single equality. It declines an outer join, a residual predicate, a key that is not a
document path or cannot be a parameter, and anything above the scan a per-batch restriction would
change the meaning of — a `LIMIT` above all, since `TOP 5` of the container is not `TOP 5` of a batch.

**Measured on a real account**, in the form and at the batch size actually emitted:
`WHERE c.category IN (@k0 … @k99)` reports `/category/?` under `UtilizedIndexes` with nothing under
`PotentialIndexes`. Had it not been index-served this would have replaced a scan with a scan plus a
hundred-term filter.

Keys are also cached for the length of one execution, since deduplication only reaches within a batch.
See section 11 for the cache that would survive a query, which is not done.

What is left is the shuffle — see *the three worth doing first* in section 11.

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

### Partial aggregates, finished above — *`COUNT(DISTINCT)` done; the rest assessed*

An aggregate that cannot be pushed whole may still be pushable as a partial the plan finishes — a
`CosmosAggregate` at the service, a combining `Aggregate` left to Calcite's runtime. Assessed against
the actual bail-outs in `CosmosAggregate`, the idea splits into four pieces of very different value:

- **The cross-partition version is already done, by the SDK.** A cross-partition aggregate or
  `GROUP BY` goes through the SDK's query pipeline, which runs partials per physical partition and
  merges them before the executor sees a row. Fanning out per feed range and combining in the adapter
  would re-implement that pipeline — including the null/undefined merge semantics it already encodes —
  for no gain. Not worth doing.
- **`COUNT(DISTINCT x)` — *done*, and it was nearly free.** `CosmosRules` registers Calcite's
  `AGGREGATE_EXPAND_DISTINCT_AGGREGATES`, which rewrites it into a two-level aggregate whose inner
  level is a plain `GROUP BY x` the existing rule pushes: the dedup happens at the service, one row
  per distinct value crosses the wire, and the count finishes wherever the outer aggregate is
  implemented. Pinned by `CosmosPartialAggregatePlannerTests`, planning for the asynchronous
  convention because the finishing count needs somewhere outside the Cosmos convention to live.
  The nullable-key caveat previously recorded here dissolved on inspection rather than needing a
  measurement: whether the service produces one absent-key group or a null-key group beside it, both
  materialize as a null row, and the finishing SQL aggregate excludes null from `COUNT(x)` either
  way — the divergence cannot reach the count. That reasoning is *reasoned, not measured*; the inner
  form it relies on (`GROUP BY` over a possibly-absent property) is one the acceptance suite already
  executes.
- **`ROLLUP`/`CUBE`/grouping sets — *medium*.** Blocked today by the `Group.SIMPLE` check. Push the
  finest-grained simple grouping, roll up above. Needs genuinely splittable calls — `COUNT`
  re-aggregates as `SUM0`, `SUM`/`MIN`/`MAX` as themselves, `AVG` decomposed into `SUM`+`COUNT` first
  by `AggregateReduceFunctionsRule` — which is what `SqlSplittableAggFunction` encodes.
- **What a split does *not* fix: the null-semantics refusals**, which are the biggest source of
  declined aggregates. A partial `SUM(c.v)` over a nullable column is `undefined` at the service —
  wrong before it is combined, so no combine repairs it. Fixing those means rewriting the rendered
  argument so Cosmos skips a null the way SQL does, which is an expression-level change orthogonal to
  splitting, and wants the same emulator measurement the current restrictions came from.

**The hazard this depended on is closed, and it was a live hole.** `CosmosAggregateRule` inspected
only the calls, so an aggregate above another aggregate — reachable by plain SQL, no expansion needed
— converted and then threw at `Implement` time, after the plan was chosen. The rule now binds its
input with `TryBindOutput` the way `CosmosSortRule` does and requires every grouping key and argument
to resolve to a document path, so an unbindable input — another aggregate, a computed projection, an
unnested element — declines at match time. The narrower shape is closed too: binding passes through
`Sort`, so the rule checks for one on the same walk and declines an aggregate above a pushed row
limit or ordering — probed first, and the planner did choose the invalid plan before the check.

**Which built-in rules belong in `CosmosRules` at all.** The registration principle the expansion
settled: a Calcite core rewrite is registered per convention only when it produces a shape that
unlocks a pushdown this convention could not otherwise reach, and a bare Volcano host — the
documented integration mode — would silently lack. General logical optimisation belongs to the host.
By that test `AGGREGATE_EXPAND_DISTINCT_AGGREGATES` qualifies, and `FILTER_AGGREGATE_TRANSPOSE` —
now registered too — turns a `HAVING` on a grouping key into a `WHERE` at the service, partition-key
recovery included. Taking it surfaced that `CosmosFilter` refused every filter above a pushed
projection, which contradicted the per-ordinal binding `CosmosProject` records: the refusal is now
per-reference — a predicate over plain paths renders, one reading a computed column is declined by
the translation — and the `WHERE` + `GROUP BY` combination joined the acceptance forms the emulator
executes. `AGGREGATE_REDUCE_FUNCTIONS` does not qualify yet, because decomposing `AVG` buys nothing
(native) and decomposing `STDDEV`/`VAR` needs a computed projection below the aggregate, which is
declined.

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

- **Declared columns — *large, and the keystone*.** A caller-declared, typed document path promoted
  to a real column: name, path, SQL type, nullability, supplied through the schema operands the way
  everything else caller-declared arrives. Not inference — the adapter's rule is against *sampling*,
  and a declaration is the same kind of trusted fact as a partition key path. Three blocked items
  converge on this, which is what makes it the keystone rather than one feature's prerequisite:
  every `UPDATE … SET` target the current row model can name is unpatchable (section 3, and the
  enumeration in `DESIGN.md` under *Updating*), the nullable-aggregate argument rewrite is
  type-directed and has no typed column to fire on (section 5), and a temporal mapping needs a
  declared basis for what a column holds (section 4). Design questions to settle in `DESIGN.md`
  first: the operand shape; what a declared column means to `INSERT` (a strategy, presumably
  `NULLABLE`); whether a declared path may be nested, which the promoted-column binding currently
  cannot carry; and what happens when the data disagrees with the declaration, which is the
  materializer's existing problem but arrives with sharper edges once the type licenses rewrites.
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
| `SupportsAggregatePushDown` | **mostly done.** Flink pushes the *local* aggregate and combines above it. `COUNT(DISTINCT)` now plans that way; grouping sets remain, and cross-partition combining was never the adapter's to do — see *Partial aggregates, finished above* in section 5. |
| `SupportsStatisticReport` | **done** — this is FLIP-231, already the model for reading statistics lazily at optimisation. |
| `SupportsPartitionPushDown` | **worth taking.** Hands the planner the list of partitions. `GetFeedRangesAsync` gives the physical ones; see *parallel scan by feed range*. |
| `SupportsDynamicFiltering` | **worth taking** — FLIP-248 dynamic partition pruning. The other half of sideways information passing: instead of fetching by key, the build side's values *prune partitions* on the probe side at run time. Complements the lookup join rather than competing with it, and for Cosmos the unit pruned is a physical partition. |
| `SupportsLookupCustomShuffle` | **worth taking, and it is the big one.** A connector says how rows should be partitioned before they reach the lookup. Shuffling build rows by Cosmos partition key would make every batch single-partition, turning a fan-out into one request — which is the saving the lookup join otherwise leaves on the table. |
| `SupportsReadingMetadata` | **small.** Metadata columns declared rather than always promoted: `_rid`, `_self`, `_attachments`, and the per-item `ttl`. Would also let `_ts`/`_etag` stop occupying ordinary column ordinals. |
| `SupportsRowLevelModificationScan` | **worth taking, and now it would pay.** The scan is told it is feeding an `UPDATE`/`DELETE`, so it can read only what the modification needs. `DELETE` is implemented and reads whole documents to use two paths out of them — `id` and the partition key — which for a map row model is the whole cost of the statement. |
| `SupportsWatermarkPushDown`, `SupportsSourceWatermark` | **only with the change feed.** Streaming concepts; the change feed is the analogue, and `_ts` the natural watermark. See *change feed*. |

### Lookup abilities

| Flink | Here |
|---|---|
| `AsyncLookupFunctionProvider` | **in progress** — this is what `CosmosLookup` is. Async is the only option here, which matches. |
| `PartialCachingLookupProvider` | **done, for one execution.** A bounded cache in front of the lookup; absence is remembered too, which is the case it most needs to hold. Scoped to a single execution deliberately: it then answers for no staleness the join did not already have. A cache *across* executions is the open part, and it is the part that needs a TTL and an owner. |
| `FullCachingLookupProvider` | **worth considering** for small containers: load the whole thing once and never call the service on a miss, with a reload strategy. A lookup table of a few thousand documents is exactly this. |
| `LookupOptions` | The options that go with the above — cache type, maximum rows, TTL, reload strategy. Worth copying the *names* so anyone who knows Flink knows these. |
| Lookup retry (FLIP-234) | **probably not.** Flink retries a lookup that comes back empty, for late-arriving reference data. The SDK already retries throttling, which is the failure that actually happens here. |

### Sink abilities

All of these bear on *Writing* above, and several map onto a Cosmos operation that is cheaper than the
obvious one:

| Flink | Here |
|---|---|
| `SupportsTargetColumnWriting` | **Patch.** Writing named columns only is exactly `PatchItemStreamAsync`, which sends the changed properties rather than the whole document. |
| `SupportsRowLevelUpdate` | Same — an `UPDATE` touching three fields should be a patch, not a replace. |
| `SupportsRowLevelDelete` | **done** — delete by `id` and partition key, which is a point operation. |
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
