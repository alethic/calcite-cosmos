using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Sort"/> to a <see cref="CosmosSort"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unusually for a conversion rule, legality here depends on container metadata rather than on
    /// the plan alone. Cosmos requires a composite index for any <c>ORDER BY</c> over two or more
    /// properties, and rejects the query outright when none matches. Converting such a sort would
    /// therefore produce a statement the service refuses, so the rule consults the container's
    /// indexing policy before firing.
    /// </para>
    /// <para>
    /// A single-key sort needs no composite index. It may be slower without one, but it runs.
    /// </para>
    /// </remarks>
    public class CosmosSortRule : CosmosConverterRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosSortRule Create(CosmosConvention convention)
        {
            return (CosmosSortRule)Config.INSTANCE
                .withConversion(typeof(Sort), new DelegatePredicate<Sort>(s => IsSupported(convention, s)), Convention.NONE, convention, "CosmosSortRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosSortRule>(c => new CosmosSortRule(c)))
                .toRule(typeof(CosmosSortRule));
        }

        /// <summary>
        /// Determines whether a sort can be pushed into the given container.
        /// </summary>
        static bool IsSupported(CosmosConvention convention, Sort sort)
        {
            var fields = CosmosImplementor.BindFields(sort.getInput().getRowType());

            if (CosmosSort.TryResolveSortKeys(sort.getCollation(), fields, sort.getInput().getRowType(), CosmosImplementor.DefaultRootAlias, out var keys, out _) == false)
                return false;

            return convention.Container.IsSortSupported(keys);
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosSortRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            var sort = (Sort)rel;

            return new CosmosSort(
                sort.getCluster(),
                sort.getTraitSet().replace(@out),
                convert(sort.getInput(), sort.getInput().getTraitSet().replace(@out)),
                sort.getCollation(),
                sort.offset,
                sort.fetch);
        }

    }

}
