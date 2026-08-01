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
        static bool IsSupported(Aggregate aggregate)
        {
            if (aggregate.getGroupType() != Aggregate.Group.SIMPLE)
                return false;

            var inputRowType = aggregate.getInput().getRowType();
            var calls = aggregate.getAggCallList();

            for (var i = 0; i < calls.size(); i++)
                if (CosmosAggregate.CanImplement((AggregateCall)calls.get(i), inputRowType) == false)
                    return false;

            return true;
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
