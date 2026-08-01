using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Traversal of an array nested within each document, rendered as <c>JOIN alias IN path</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Despite the keyword, Cosmos <c>JOIN</c> is not a relational join — it has no predicate in
    /// the grammar at all. It cross-products a document with one of its own arrays, which is
    /// <c>UNNEST</c> spelled <c>JOIN</c>. This node therefore arises from <c>Uncollect</c> and
    /// <c>Correlate</c>, never from a <c>Join</c>.
    /// </para>
    /// <para>
    /// The output row type is the input's fields followed by one field carrying the array element.
    /// </para>
    /// </remarks>
    public class CosmosUnnest : SingleRel, CosmosRel
    {

        readonly RexNode _array;
        readonly RelDataType _rowType;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="array">The expression producing the array, addressed against the input's row type.</param>
        /// <param name="rowType">The output row type: the input's fields plus the element field.</param>
        /// <exception cref="ArgumentNullException"><paramref name="array"/> or <paramref name="rowType"/> is <c>null</c>.</exception>
        public CosmosUnnest(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RexNode array, RelDataType rowType) :
            base(cluster, traitSet, input)
        {
            _array = array ?? throw new ArgumentNullException(nameof(array));
            _rowType = rowType ?? throw new ArgumentNullException(nameof(rowType));
        }

        /// <summary>
        /// Gets the expression producing the array being traversed.
        /// </summary>
        public RexNode Array => _array;

        /// <inheritdoc />
        protected override RelDataType deriveRowType()
        {
            return _rowType;
        }

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosUnnest(getCluster(), traitSet, (RelNode)inputs.get(0), _array, _rowType);
        }

        /// <inheritdoc />
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            return base.computeSelfCost(planner, mq)?.multiplyBy(CosmosConvention.CostMultiplier);
        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            implementor.Visit(getInput());

            // JOIN belongs to the FROM clause, which is fixed before SELECT is chosen.
            if (implementor.Query.HasProjection)
                throw new CosmosTranslationException("An array traversal cannot be applied above a pushed-down projection.");

            if (implementor.CreateTranslator().TryResolvePath(_array, out var path) == false)
                throw new CosmosTranslationException("The traversed array does not resolve to a document path.");

            var alias = implementor.CreateUnnestAlias();
            implementor.Query.AddUnnest(alias, path!.ToString());

            // The element is addressed by its alias; the input's bindings carry through unchanged.
            var fields = new List<CosmosPath>(implementor.Fields) { CosmosPath.Root(alias) };
            implementor.Fields = fields;
        }

    }

}
