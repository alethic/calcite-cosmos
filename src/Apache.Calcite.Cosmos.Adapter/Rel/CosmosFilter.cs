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
            var fields = CosmosImplementor.BindFields(getInput().getRowType());

            var multiplier = CosmosConvention.CostMultiplier;

            if (CosmosPartitionKeyExtractor.TryExtract(getCondition(), fields, container, CosmosImplementor.DefaultRootAlias, out _))
                multiplier *= SinglePartitionDiscount;

            if (ReferencesUnindexedPath(getCondition(), fields, container))
                multiplier *= UnindexedPathPenalty;

            return cost.multiplyBy(multiplier);
        }

        /// <summary>
        /// Applied when the predicate confines the query to one logical partition.
        /// </summary>
        public const double SinglePartitionDiscount = .25d;

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

            // Cosmos evaluates WHERE against the source document, before SELECT. A filter sitting
            // above a pushed-down projection therefore cannot be expressed without inlining the
            // projection's expressions into the predicate, which is not attempted.
            if (implementor.Query.HasProjection)
                throw new CosmosTranslationException("A filter cannot be applied above a pushed-down projection.");

            // WHERE is evaluated before OFFSET/LIMIT, so folding a filter that the plan places
            // above a row restriction would filter the whole set and then restrict it, rather
            // than restricting first.
            if (implementor.Query.HasRowLimit)
                throw new CosmosTranslationException("A filter cannot be applied above a pushed-down row limit.");

            // Recovered before translation, from the same bindings, so that a query naming its
            // partition key can be executed against one partition instead of fanned out.
            if (implementor.PartitionKeyValues is null &&
                Metadata.CosmosPartitionKeyExtractor.TryExtract(getCondition(), implementor.Fields, implementor.Container, implementor.RootAlias, out var partitionKey))
                implementor.PartitionKeyValues = partitionKey;

            var condition = implementor.Translate(getCondition());

            // Stacked filters are normally merged by the planner, but conjoin defensively rather
            // than silently discarding one.
            implementor.Query.Where = string.IsNullOrEmpty(implementor.Query.Where)
                ? condition
                : $"({implementor.Query.Where} AND {condition})";
        }

    }

}
