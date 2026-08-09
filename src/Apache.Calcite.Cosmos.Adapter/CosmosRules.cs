using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Rel.Convert;

using org.apache.calcite.plan;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// The conversion rules registered for a <see cref="CosmosConvention"/> instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules are per-convention rather than static, because a convention is bound to a container
    /// and some rules must consult that container's metadata to decide whether they may fire.
    /// </para>
    /// <para>
    /// Several operators are deliberately absent and must not be added without revisiting the
    /// design:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>No join rule into this convention.</b> Cosmos <c>JOIN</c> has no predicate — it
    /// cross-products a document with its own nested arrays. Relational joins are inexpressible as a
    /// statement, and array traversal arrives via <c>Uncollect</c>/<c>Correlate</c> instead.
    /// <para>
    /// <see cref="Rel.Convert.CosmosLookupJoinRule"/> is not a counter-example. It converts a join
    /// into <c>ClrAsyncEnumerableConvention</c>, not into this one: the join is still performed
    /// outside the service, and all that reaches the statement is a restriction to the keys one side
    /// actually has.
    /// </para>
    /// </description></item>
    /// <item><description>
    /// <b>No set operation rules.</b> Cosmos has no <c>UNION</c>, <c>INTERSECT</c>, or
    /// <c>EXCEPT</c>. These are evaluated by Calcite in-process.
    /// </description></item>
    /// <item><description>
    /// <b>No values rule.</b> There is no container-independent row source.
    /// </description></item>
    /// <item><description>
    /// <b>One way out, and it is asynchronous.</b> There is no converter into
    /// <c>ClrEnumerableConvention</c> or Calcite's <c>EnumerableConvention</c>, because the Cosmos
    /// SDK has no synchronous data-plane API and such a converter could only block a thread per
    /// page. A query over a Cosmos table plans only when the root is asked for in
    /// <c>ClrAsyncEnumerableConvention</c>.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class CosmosRules
    {

        /// <summary>
        /// Returns the rules to register for the given convention.
        /// </summary>
        /// <param name="convention">The convention the rules are bound to.</param>
        /// <returns>The rules.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="convention"/> is <c>null</c>.</exception>
        public static IEnumerable<RelOptRule> GetRules(CosmosConvention convention)
        {
            if (convention is null)
                throw new ArgumentNullException(nameof(convention));

            yield return CosmosAggregateRule.Create(convention);

            // Not a conversion, and not Cosmos's: Calcite's own rewrite of COUNT(DISTINCT x) into an
            // aggregate over an aggregate, registered because a bare Volcano planner has no logical
            // rewrites at all. Its inner half is a plain GROUP BY the rule above can push, so the
            // dedup happens at the service and one row per distinct value crosses the wire; the
            // count finishes wherever the outer aggregate is implemented, which is never here — the
            // aggregate rule declines an input it cannot bind, and aggregate output binds nothing.
            // A static instance, so registering it once per convention registers it once.
            yield return org.apache.calcite.rel.rules.CoreRules.AGGREGATE_EXPAND_DISTINCT_AGGREGATES;

            yield return CosmosFilterRule.Create(convention);

            // Partial pushdown: where only some of a predicate renders, the service still evaluates
            // that part rather than the plan declining the whole thing and scanning the container.
            yield return CosmosFilterSplitRule.Create(convention);

            // Ordering by a scoring function, which Calcite expresses as three nodes and Cosmos as one
            // clause — and whose middle node, a projected score, is a statement the service rejects.
            yield return CosmosRankRule.Create(convention);
            yield return CosmosProjectRule.Create(convention);
            yield return CosmosSortRule.Create(convention);
            yield return CosmosUnnestRule.Create(convention);

            // The way out. Without it a pushed-down subtree is a statement nothing can read the rows of,
            // and the planner has no complete plan to choose.
            yield return CosmosToClrAsyncEnumerableConverterRule.Create(convention);

            // The other way out, and the only one that reads less than the whole container: a join
            // whose other side supplies the keys. It leaves the convention for the same reason the
            // converter does — the join happens here, not at the service — so it is registered last,
            // alongside it.
            //
            // Takes no convention, and for the same reason the modify below does not: a converter rule
            // between two container-independent conventions cannot have one instance per container,
            // because the instances are indistinguishable to a planner. It reads the container from the
            // join's probe side instead. See the rule.
            yield return CosmosLookupJoinRule.Create();

            // Writing, which never enters this convention at all: Cosmos SQL has no DML, so a write is
            // item CRUD over rows rather than a statement. Reads the container from the modify, for the
            // reason just given. See the rule.
            yield return CosmosTableModifyRule.Create();
        }

    }

}
