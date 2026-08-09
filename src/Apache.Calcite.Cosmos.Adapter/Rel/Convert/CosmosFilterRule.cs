using Apache.Calcite.Cosmos.Adapter.Sql;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Filter"/> to a <see cref="CosmosFilter"/>.
    /// </summary>
    /// <remarks>
    /// The rule declines any filter whose condition has no Cosmos equivalent, so that an
    /// untranslatable predicate is evaluated in-process rather than failing during
    /// implementation.
    /// </remarks>
    public class CosmosFilterRule : CosmosConverterRule
    {

        /// <summary>
        /// Determines whether a filter's condition can be rendered as Cosmos SQL.
        /// </summary>
        /// <remarks>
        /// Translation is attempted against a throwaway parameter list, using the binding derived by
        /// walking the input. Deriving it from the input row type instead would read alias names as
        /// document properties above a projection, and answer for paths the container does not have.
        /// </remarks>
        static bool IsTranslatable(Filter filter)
        {
            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields) == false)
                return false;

            var translator = new CosmosRexTranslator(filter.getCluster().getRexBuilder(), fields, new CosmosParameterList());
            return translator.TryTranslate(filter.getCondition(), out _);
        }

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosFilterRule Create(CosmosConvention convention)
        {
            return (CosmosFilterRule)Config.INSTANCE
                .withConversion(typeof(Filter), new DelegatePredicate<Filter>(IsTranslatable), Convention.NONE, convention, "CosmosFilterRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosFilterRule>(c => new CosmosFilterRule(c)))
                .toRule(typeof(CosmosFilterRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosFilterRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var filter = (Filter)rel;

            return new CosmosFilter(
                filter.getCluster(),
                filter.getTraitSet().replace(@out),
                convert(filter.getInput(), filter.getInput().getTraitSet().replace(@out)),
                filter.getCondition());
        }

    }

}
