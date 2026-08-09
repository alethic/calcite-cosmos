using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.logical;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Splits a grouping-set aggregation into a simple aggregation Cosmos can take and a rollup the
    /// plan finishes, so that one row per group crosses the wire instead of one per document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ROLLUP</c> and <c>CUBE</c> have no Cosmos rendering — the service groups one way per
    /// statement — so a grouping-set aggregate is declined whole and evaluated in process over
    /// everything the scan returns. But every grouping set is a coarsening of the finest one, which
    /// is the aggregate's full key set and <em>is</em> a simple <c>GROUP BY</c>: pushing that and
    /// re-aggregating its partials above yields the same rows, because each source row lands in
    /// exactly one finest group.
    /// </para>
    /// <para>
    /// Re-aggregation changes the calls: a partial <c>COUNT</c> is finished by summing, not by
    /// counting the groups, so <c>COUNT</c> becomes <c>$SUM0</c> above — zero for an empty grand
    /// total, as <c>COUNT</c> is. <c>SUM</c>, <c>MIN</c> and <c>MAX</c> finish as themselves.
    /// <c>AVG</c> does not finish at all — an average of averages weights every group equally — and
    /// declines the split; decomposed into <c>SUM</c> and <c>COUNT</c> it would, which is
    /// <c>AGGREGATE_REDUCE_FUNCTIONS</c> and a decision recorded in <c>CosmosRules</c>.
    /// </para>
    /// <para>
    /// The split fires only where the simple half would actually convert — the same question
    /// <see cref="CosmosAggregateRule"/> asks, asked of a node that does not exist yet — and it
    /// terminates because the rollup it leaves behind reads an aggregate, which binds to nothing.
    /// </para>
    /// </remarks>
    public class CosmosAggregateSplitRule : RelOptRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits aggregates over.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosAggregateSplitRule Create(CosmosConvention convention)
        {
            return new CosmosAggregateSplitRule(convention);
        }

        readonly CosmosConvention _convention;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits aggregates over.</param>
        // See CosmosFilterSplitRule for why the deprecated operand builders are used.
#pragma warning disable CS0612
        public CosmosAggregateSplitRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(Aggregate), any()),
                "CosmosAggregateSplitRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <summary>
        /// Returns the container a subtree reads, or <c>null</c> where it does not read one.
        /// </summary>
        static CosmosTable? FindTable(RelNode? node)
        {
            if (node is org.apache.calcite.plan.volcano.RelSubset subset)
                node = subset.getOriginal() ?? subset.getBest();

            return node switch
            {
                TableScan scan => scan.getTable()?.unwrap(typeof(CosmosTable)) as CosmosTable,
                Filter filter => FindTable(filter.getInput()),
                Project project => FindTable(project.getInput()),
                Correlate correlate => FindTable(correlate.getLeft()),
                _ => null,
            };
        }

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            var aggregate = (Aggregate)call.rel(0);

            if (aggregate.getGroupType() == Aggregate.Group.SIMPLE)
                return false;

            if (FindTable(aggregate.getInput()) is not CosmosTable table || ReferenceEquals(table.Convention, _convention) == false)
                return false;

            if (CosmosAggregateRule.CanPush(aggregate.getInput(), aggregate.getGroupSet(), aggregate.getAggCallList()) == false)
                return false;

            var calls = aggregate.getAggCallList();
            for (var i = 0; i < calls.size(); i++)
                if (IsSplittable((AggregateCall)calls.get(i)) == false)
                    return false;

            return true;
        }

        /// <summary>
        /// Determines whether a call's partials can be re-aggregated into the call's own result.
        /// </summary>
        static bool IsSplittable(AggregateCall call)
        {
            switch ((SqlKind.__Enum)call.getAggregation().getKind().ordinal())
            {
                case SqlKind.__Enum.COUNT:
                case SqlKind.__Enum.SUM:
                case SqlKind.__Enum.MIN:
                case SqlKind.__Enum.MAX:
                    return true;

                default:
                    return false;
            }
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var aggregate = (Aggregate)call.rel(0);

            // The finest grouping — the aggregate's full key set — as a simple GROUP BY, with the
            // original calls as its partials.
            var bottom = LogicalAggregate.create(
                aggregate.getInput(),
                aggregate.getHints(),
                aggregate.getGroupSet(),
                null,
                aggregate.getAggCallList());

            // The bottom's output is its keys in ascending ordinal order, then its calls; the
            // original grouping sets are re-expressed against those positions.
            var keys = aggregate.getGroupSet().asList();
            var keyCount = keys.size();

            var topGroupSets = new java.util.ArrayList();
            var originalSets = aggregate.getGroupSets();
            for (var i = 0; i < originalSets.size(); i++)
            {
                var mapped = ImmutableBitSet.builder();
                var ordinals = ((ImmutableBitSet)originalSets.get(i)).asList();
                for (var j = 0; j < ordinals.size(); j++)
                    mapped.set(keys.indexOf(ordinals.get(j)));

                topGroupSets.add(mapped.build());
            }

            var topCalls = new java.util.ArrayList();
            var calls = aggregate.getAggCallList();
            for (var i = 0; i < calls.size(); i++)
                topCalls.add(Finish((AggregateCall)calls.get(i), keyCount + i, keyCount, bottom));

            var top = LogicalAggregate.create(
                bottom,
                aggregate.getHints(),
                ImmutableBitSet.range(0, keyCount),
                com.google.common.collect.ImmutableList.copyOf(topGroupSets),
                topCalls);

            call.transformTo(top);
        }

        /// <summary>
        /// Returns the call that finishes a partial found at the given bottom-output ordinal.
        /// </summary>
        /// <remarks>
        /// The original call's type is kept in every case, because the rewrite must produce the row
        /// type the aggregate it replaces had.
        /// </remarks>
        static AggregateCall Finish(AggregateCall call, int ordinal, int groupCount, RelNode input)
        {
            var argument = java.util.Collections.singletonList(java.lang.Integer.valueOf(ordinal));

            if ((SqlKind.__Enum)call.getAggregation().getKind().ordinal() == SqlKind.__Enum.COUNT)
                return AggregateCall.create(
                    SqlStdOperatorTable.SUM0,
                    false,
                    false,
                    argument,
                    -1,
                    org.apache.calcite.rel.RelCollations.EMPTY,
                    groupCount,
                    input,
                    call.getType(),
                    call.getName());

            return call.copy(argument, -1, org.apache.calcite.rel.RelCollations.EMPTY);
        }

    }

}
