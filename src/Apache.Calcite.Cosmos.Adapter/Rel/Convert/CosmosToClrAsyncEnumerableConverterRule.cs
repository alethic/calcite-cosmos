using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;

using java.util.function;

using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Rule to convert a relational expression from <see cref="CosmosConvention"/> to
    /// <see cref="ClrAsyncEnumerableConvention"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a <see cref="CosmosConverterRule"/>: that base is for rules converting a standard operator
    /// <em>into</em> the Cosmos convention, and carries the obligation to decline anything Cosmos cannot
    /// express. This one converts out, and has nothing to decline. Every subtree in the convention got
    /// there by a rule that already established it renders — the operators that do not are the ones that
    /// were never converted in.
    /// </para>
    /// <para>
    /// It is the only route out. There is deliberately no rule into
    /// <see cref="Extensions.Adapter.Enumerable.ClrEnumerableConvention"/> or into Calcite's own
    /// <c>EnumerableConvention</c>, for the reason
    /// <see cref="CosmosToClrAsyncEnumerableConverter"/> gives.
    /// </para>
    /// </remarks>
    public class CosmosToClrAsyncEnumerableConverterRule : ConverterRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose nodes will be converted.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosToClrAsyncEnumerableConverterRule Create(CosmosConvention convention)
        {
            return (CosmosToClrAsyncEnumerableConverterRule)Config.INSTANCE
                .withConversion(typeof(RelNode), convention, ClrAsyncEnumerableConvention.Instance, "CosmosToClrAsyncEnumerableConverterRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosToClrAsyncEnumerableConverterRule>(c => new CosmosToClrAsyncEnumerableConverterRule(c)))
                .toRule(typeof(CosmosToClrAsyncEnumerableConverterRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosToClrAsyncEnumerableConverterRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode convert(RelNode rel)
        {
            return new CosmosToClrAsyncEnumerableConverter(rel.getCluster(), rel.getTraitSet().replace(getOutConvention()), rel);
        }

    }

}
