using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts an <see cref="Aggregate"/> to a <see cref="CosmosAggregate"/>.
    /// </summary>
    /// <remarks>
    /// Only aggregates whose Cosmos semantics match SQL's are converted; see
    /// <see cref="CosmosAggregate"/> for what that excludes and why. Everything else is left to
    /// Calcite's own runtime.
    /// </remarks>
    public class CosmosAggregateRule : CosmosConverterRule
    {

        /// <summary>
        /// Determines whether an aggregate can be pushed down in full.
        /// </summary>
        /// <remarks>
        /// Bound the way implementation will bind, so the answer here is final — the same reasoning
        /// as <see cref="CosmosSortRule"/>. An input whose output is not addressable as document
        /// paths — another aggregate, a computed projection, an unnested element — cannot carry the
        /// grouping keys or the aggregate arguments, and firing anyway would only surface as a
        /// translation failure after the plan is chosen. An aggregate above a pushed aggregate is
        /// the case that matters: <c>AGGREGATE_EXPAND_DISTINCT_AGGREGATES</c> produces exactly that
        /// shape, and only the inner half is Cosmos's to take.
        /// </remarks>
        static bool IsSupported(Aggregate aggregate)
        {
            if (aggregate.getGroupType() != Aggregate.Group.SIMPLE)
                return false;

            return CanPush(aggregate.getInput(), aggregate.getGroupSet(), aggregate.getAggCallList());
        }

        /// <summary>
        /// Determines whether a simple aggregation over the given input, keys and calls can be
        /// rendered faithfully. Shared with <see cref="CosmosAggregateSplitRule"/>, which asks the
        /// question of the bottom half it would create rather than of a node that exists.
        /// </summary>
        internal static bool CanPush(RelNode input, org.apache.calcite.util.ImmutableBitSet groupSet, java.util.List calls)
        {
            if (CosmosImplementor.TryBindOutput(input, out var fields) == false)
                return false;

            // Binding passes through a sort, but an aggregate cannot: Cosmos rejects GROUP BY with
            // ORDER BY in one statement, and applies GROUP BY before OFFSET/LIMIT, so grouping above
            // a pushed row restriction would group the container rather than the restriction. The
            // same conditions implementation refuses, decided here instead.
            if (ReadsThroughASort(input))
                return false;

            // An aggregate over an aggregate, which binding now permits for the call-less case —
            // a DISTINCT's output is paths, so a *sort* above one is sound. An aggregate above one
            // is not: there is one SELECT per statement, so folding the outer in would clear the
            // projection while leaving the DISTINCT behind, and count the rows of the container
            // rather than the distinct values. That is the COUNT(DISTINCT) shape, whose outer half
            // belongs outside the convention.
            if (ReadsThroughAnAggregate(input))
                return false;

            var groupKeys = groupSet.asList();
            for (var i = 0; i < groupKeys.size(); i++)
                if (Resolves(fields, (java.lang.Integer)groupKeys.get(i)) == false)
                    return false;

            var inputRowType = input.getRowType();

            for (var i = 0; i < calls.size(); i++)
            {
                var call = (AggregateCall)calls.get(i);
                if (CosmosAggregate.CanImplement(call, inputRowType) == false)
                    return false;

                var arguments = call.getArgList();
                for (var j = 0; j < arguments.size(); j++)
                    if (Resolves(fields, (java.lang.Integer)arguments.get(j)) == false)
                        return false;
            }

            return true;
        }

        static bool Resolves(System.Collections.Generic.IReadOnlyList<Sql.CosmosPath?> fields, java.lang.Integer ordinal)
        {
            var index = ordinal.intValue();
            return index >= 0 && index < fields.Count && fields[index] is not null;
        }

        /// <summary>
        /// Determines whether a sort lies on the path the aggregate's input binds through.
        /// </summary>
        /// <remarks>
        /// The traversal mirrors <see cref="CosmosImplementor.TryBindOutput"/>: the same nodes are
        /// passed through, so a sort this walk cannot see is a sort binding cannot see either.
        /// </remarks>
        static bool ReadsThroughASort(RelNode? node)
        {
            if (node is org.apache.calcite.plan.volcano.RelSubset subset)
                node = subset.getOriginal() ?? subset.getBest();

            return node switch
            {
                Sort => true,
                Filter filter => ReadsThroughASort(filter.getInput()),
                Project project => ReadsThroughASort(project.getInput()),
                Correlate correlate => ReadsThroughASort(correlate.getLeft()),
                _ => false,
            };
        }

        /// <summary>
        /// Determines whether an aggregate lies on the path the input binds through.
        /// </summary>
        static bool ReadsThroughAnAggregate(RelNode? node)
        {
            if (node is org.apache.calcite.plan.volcano.RelSubset subset)
                node = subset.getOriginal() ?? subset.getBest();

            return node switch
            {
                Aggregate => true,
                Filter filter => ReadsThroughAnAggregate(filter.getInput()),
                Project project => ReadsThroughAnAggregate(project.getInput()),
                Correlate correlate => ReadsThroughAnAggregate(correlate.getLeft()),
                _ => false,
            };
        }

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosAggregateRule Create(CosmosConvention convention)
        {
            return (CosmosAggregateRule)Config.INSTANCE
                .withConversion(typeof(Aggregate), new DelegatePredicate<Aggregate>(IsSupported), Convention.NONE, convention, "CosmosAggregateRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosAggregateRule>(c => new CosmosAggregateRule(c)))
                .toRule(typeof(CosmosAggregateRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosAggregateRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var aggregate = (Aggregate)rel;

            return new CosmosAggregate(
                aggregate.getCluster(),
                aggregate.getTraitSet().replace(@out),
                convert(aggregate.getInput(), aggregate.getInput().getTraitSet().replace(@out)),
                aggregate.getGroupSet(),
                aggregate.getGroupSets(),
                aggregate.getAggCallList());
        }

    }

}
