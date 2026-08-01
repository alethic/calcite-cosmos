using Apache.Calcite.Cosmos.Adapter.Sql;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a lateral <see cref="Correlate"/> over an <see cref="Uncollect"/> into a
    /// <see cref="CosmosUnnest"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the only rule that produces a Cosmos <c>JOIN</c>, and it deliberately matches
    /// <see cref="Correlate"/> rather than <see cref="Join"/>. Cosmos <c>JOIN</c> has no predicate
    /// in its grammar; it cross-products a document with one of its own arrays. A relational join
    /// is not expressible and no rule may convert one.
    /// </para>
    /// <para>
    /// The shape produced for <c>UNNEST</c> is a correlate whose right input is an
    /// <c>Uncollect</c> over a single-expression <c>Project</c> over a one-row <c>Values</c>. The
    /// projected expression addresses the correlation variable and must resolve to a path.
    /// </para>
    /// </remarks>
    public class CosmosUnnestRule : CosmosConverterRule
    {

        /// <summary>
        /// Extracts the array expression from the right side of a correlate, or returns <c>null</c>
        /// when the node is not a lateral array traversal this rule can express.
        /// </summary>
        /// <remarks>
        /// Public so that the shape this rule depends on can be asserted against real planner
        /// output rather than assumed. Planning <c>… FROM products AS c, UNNEST(c."_MAP"['tags'])</c>
        /// yields a correlate over <c>Uncollect(Project(ITEM($cor0._MAP, 'tags')))</c>, which is
        /// what this recognises.
        /// </remarks>
        /// <param name="correlate">The correlate to inspect.</param>
        /// <returns>The array expression, or <c>null</c>.</returns>
        public static RexNode? GetArrayExpression(Correlate correlate)
        {
            if (Strip(correlate.getRight()) is not Uncollect uncollect)
                return null;

            // Cosmos has no way to surface an element's position.
            if (uncollect.withOrdinality)
                return null;

            if (Strip(uncollect.getInput()) is not Project project || project.getProjects().size() != 1)
                return null;

            return (RexNode)project.getProjects().get(0);
        }

        /// <summary>
        /// Resolves an input to a concrete node.
        /// </summary>
        /// <remarks>
        /// Once a tree is registered with the Volcano planner, an operator's inputs are equivalence
        /// sets rather than the nodes themselves, so a rule inspecting more than its own node has
        /// to see through them. This is why the rule is written against the whole correlate: the
        /// shape it needs spans three levels, and only the top one is bound directly.
        /// </remarks>
        static RelNode? Strip(RelNode? node)
        {
            for (var i = 0; node is org.apache.calcite.plan.volcano.RelSubset subset && i < 8; i++)
                node = subset.getBest() ?? subset.getOriginal();

            return node;
        }

        /// <summary>
        /// Determines whether the correlate is an array traversal whose array resolves to a path on
        /// the left input.
        /// </summary>
        static bool IsTranslatable(Correlate correlate)
        {
            var array = GetArrayExpression(correlate);
            if (array is null)
                return false;

            // Only an inner traversal matches Cosmos's cross-product semantics; a left join would
            // have to preserve documents whose array is empty, which JOIN … IN does not.
            if (correlate.getJoinType() != JoinRelType.INNER)
                return false;

            var fields = CosmosImplementor.BindFields(correlate.getLeft().getRowType());
            var translator = new CosmosRexTranslator(correlate.getCluster().getRexBuilder(), fields, new CosmosParameterList());

            return translator.TryResolvePath(array, out _);
        }

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosUnnestRule Create(CosmosConvention convention)
        {
            return (CosmosUnnestRule)Config.INSTANCE
                .withConversion(typeof(Correlate), new DelegatePredicate<Correlate>(IsTranslatable), Convention.NONE, convention, "CosmosUnnestRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosUnnestRule>(c => new CosmosUnnestRule(c)))
                .toRule(typeof(CosmosUnnestRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosUnnestRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode? convert(RelNode rel)
        {
            var correlate = (Correlate)rel;

            var array = GetArrayExpression(correlate);
            if (array is null)
                return null;

            var left = correlate.getLeft();

            return new CosmosUnnest(
                correlate.getCluster(),
                correlate.getTraitSet().replace(@out),
                convert(left, left.getTraitSet().replace(@out)),
                array,
                correlate.getRowType());
        }

    }

}
