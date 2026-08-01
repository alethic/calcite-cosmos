using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
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

            var condition = implementor.Translate(getCondition());

            // Stacked filters are normally merged by the planner, but conjoin defensively rather
            // than silently discarding one.
            implementor.Query.Where = string.IsNullOrEmpty(implementor.Query.Where)
                ? condition
                : $"({implementor.Query.Where} AND {condition})";
        }

    }

}
