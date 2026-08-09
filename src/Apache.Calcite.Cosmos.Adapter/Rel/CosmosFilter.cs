using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Filter implemented in the <see cref="CosmosConvention"/> calling convention, rendered as a
    /// <c>WHERE</c> clause.
    /// </summary>
    public class CosmosFilter : Filter, CosmosRel
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="condition">The filter condition.</param>
        public CosmosFilter(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RexNode condition) :
            base(cluster, traitSet, input, condition)
        {

        }

        /// <inheritdoc />
        public override Filter copy(RelTraitSet traitSet, RelNode input, RexNode condition)
        {
            return new CosmosFilter(getCluster(), traitSet, input, condition);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Two properties of the predicate dominate what a Cosmos query costs, and neither is
        /// visible in the shape of the plan:
        /// </para>
        /// <list type="bullet">
        /// <item><description>
        /// Naming the partition key confines execution to one physical partition instead of
        /// fanning out across every one and merging.
        /// </description></item>
        /// <item><description>
        /// Filtering on a path the container does not index forces a scan of it.
        /// </description></item>
        /// </list>
        /// <para>
        /// Both are reflected here so the planner prefers the predicate that is genuinely cheaper
        /// rather than treating all pushed-down filters alike.
        /// </para>
        /// </remarks>
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var cost = base.computeSelfCost(planner, mq);
            if (cost is null || getConvention() is not CosmosConvention convention)
                return cost;

            var container = convention.Container;

            // Cost only, so an input whose binding cannot be derived costs as the base does rather than
            // declining anything — but it is the walked binding, so the discount is decided on the paths
            // the statement will actually address.
            if (CosmosImplementor.TryBindOutput(getInput(), out var fields) == false)
                return cost.multiplyBy(CosmosConvention.CostMultiplier);

            var multiplier = CosmosConvention.CostMultiplier;

            // How much pinning the key is worth is a fact about the container, not a constant. A
            // cross-partition query costs roughly one single-partition query per physical partition, so
            // on a two-partition container this saves little and on a two-hundred-partition container it
            // saves nearly everything. Where the service was not asked, the old constant stands.
            if (CosmosPartitionKeyExtractor.TryExtractPrefix(getCondition(), fields, container, CosmosImplementor.DefaultRootAlias, out var prefix, out var complete))
                multiplier *= PartitionDiscount(container, prefix.Count, complete);

            if (ReferencesUnindexedPath(getCondition(), fields, container))
                multiplier *= UnindexedPathPenalty;

            return cost.multiplyBy(multiplier);
        }

        /// <summary>
        /// Applied when the predicate confines the query to one logical partition and the container's
        /// spread is unknown.
        /// </summary>
        public const double SinglePartitionDiscount = .25d;

        /// <summary>
        /// Returns the discount for confining a query to part of the container.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A complete key reaches one partition out of however many there are, so the work is divided by
        /// that count. A prefix of a hierarchical key reaches a subset, and how large a subset is not
        /// knowable from anything declared — it depends on how the values are distributed. Half the
        /// benefit of a complete key is a guess, and it is a guess about <em>cost</em>: it can make the
        /// planner prefer the wrong plan and cannot make it return the wrong rows.
        /// </para>
        /// <para>
        /// Bounded below so that an enormous container does not drive a filter's cost to nothing and
        /// make every plan containing one look free.
        /// </para>
        /// </remarks>
        static double PartitionDiscount(CosmosContainerMetadata container, int pinned, bool complete)
        {
            if (container.Statistics is not CosmosContainerStatistics statistics || statistics.PartitionCount <= 1)
                return complete ? SinglePartitionDiscount : 1d;

            var whole = 1d / statistics.PartitionCount;

            return System.Math.Max(complete ? whole : System.Math.Sqrt(whole), MinimumPartitionDiscount);
        }

        /// <summary>
        /// The floor on <see cref="PartitionDiscount"/>.
        /// </summary>
        public const double MinimumPartitionDiscount = .001d;

        /// <summary>
        /// Applied when the predicate touches a path the container does not index.
        /// </summary>
        public const double UnindexedPathPenalty = 4d;

        /// <summary>
        /// Determines whether a predicate references any path outside the container's index.
        /// </summary>
        static bool ReferencesUnindexedPath(RexNode condition, IReadOnlyList<CosmosPath?> fields, CosmosContainerMetadata container)
        {
            var translator = new CosmosRexTranslator(RexBuilderHolder.Value, fields, new CosmosParameterList());
            var unindexed = false;

            void Walk(RexNode node)
            {
                if (unindexed)
                    return;

                if (translator.TryResolvePath(node, out var path) && path is not null)
                {
                    // Only container-rooted paths correspond to index paths; an element-relative
                    // one is reached through its array, which is covered separately.
                    if (string.Equals(path.Alias, CosmosImplementor.DefaultRootAlias, StringComparison.Ordinal) &&
                        container.IsPathIndexed(path.ToPolicyPath()) == false)
                        unindexed = true;

                    return;
                }

                if (node is RexCall call)
                    for (var i = 0; i < call.getOperands().size(); i++)
                        Walk((RexNode)call.getOperands().get(i));
            }

            Walk(condition);
            return unindexed;
        }

        static class RexBuilderHolder
        {

            internal static readonly RexBuilder Value = new(new org.apache.calcite.jdbc.JavaTypeFactoryImpl());

        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            implementor.Visit(getInput());

            // Cosmos evaluates WHERE against the source document, before SELECT — which is not a
            // reason to refuse a filter above a pushed-down projection, because the translation
            // below renders the predicate against document paths rather than projected names, and
            // filtering before or after a path-only projection admits the same documents. What
            // cannot be expressed is a predicate reading a computed column, which has no path for
            // WHERE to name; the translation refuses that reference itself. Filters above a
            // projection were once refused wholesale here, which contradicted the per-ordinal
            // binding CosmosProject records, and cost the WHERE that FILTER_AGGREGATE_TRANSPOSE
            // recovers from a HAVING on a grouping key.

            // WHERE is evaluated before OFFSET/LIMIT, so folding a filter that the plan places
            // above a row restriction would filter the whole set and then restrict it, rather
            // than restricting first.
            if (implementor.Query.HasRowLimit)
                throw new CosmosTranslationException("A filter cannot be applied above a pushed-down row limit.");

            // Recovered before translation, from the same bindings, so that a query naming its
            // partition key can be executed against one partition instead of fanned out.
            if (implementor.PartitionKeyValues is null &&
                Metadata.CosmosPartitionKeyExtractor.TryExtractPrefix(getCondition(), implementor.Fields, implementor.Container, implementor.RootAlias, out var partitionKey, out var complete))
            {
                implementor.PartitionKeyValues = partitionKey;
                implementor.PartitionKeyIsComplete = complete;
            }

            // And whether the predicate is only that, plus an id — the shape a point read answers for
            // about 1 RU rather than the 2.3 a query costs at best. Offered here; whether it is taken
            // depends on what the rest of the statement asks for, which the implementor decides.
            if (implementor.PointReadCandidate is null &&
                Metadata.CosmosPartitionKeyExtractor.TryExtractPointRead(getCondition(), implementor.Fields, implementor.Container, implementor.RootAlias, out _, out var pointReadId))
                implementor.PointReadCandidate = pointReadId;

            var condition = implementor.Translate(getCondition());

            // Stacked filters are normally merged by the planner, but conjoin defensively rather
            // than silently discarding one.
            implementor.Query.Where = string.IsNullOrEmpty(implementor.Query.Where)
                ? condition
                : $"({implementor.Query.Where} AND {condition})";
        }

    }

}
