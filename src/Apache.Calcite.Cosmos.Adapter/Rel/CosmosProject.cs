using System;

using Apache.Calcite.Cosmos.Adapter.Sql;

using com.google.common.collect;

using java.util;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Projection implemented in the <see cref="CosmosConvention"/> calling convention.
    /// </summary>
    /// <remarks>
    /// Rendered as <c>SELECT VALUE { name: expression, … }</c>. Cosmos treats a flat select list as
    /// sugar for exactly this object constructor, and emitting the explicit form keeps the result
    /// shape uniform: one JSON value per row, keyed by output field name, whatever the arity.
    /// </remarks>
    public class CosmosProject : Project, CosmosRel
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="projects">The projected expressions.</param>
        /// <param name="rowType">The output row type.</param>
        public CosmosProject(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, List projects, RelDataType rowType) :
            base(cluster, traitSet, ImmutableList.of(), input, projects, rowType, ImmutableSet.of())
        {

        }

        /// <inheritdoc />
        public override Project copy(RelTraitSet traitSet, RelNode input, List projects, RelDataType rowType)
        {
            return new CosmosProject(getCluster(), traitSet, input, projects, rowType);
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

            // Without derived tables there is nowhere to nest a second projection.
            if (implementor.Query.HasProjection)
                throw new CosmosTranslationException("A projection has already been applied.");

            var projects = getProjects();
            if (projects.size() == 0)
                throw new CosmosTranslationException("An empty projection has no Cosmos equivalent.");

            var names = getRowType().getFieldNames();

            // Bound to the input field bindings; the rebinding below happens only once every
            // expression has been translated against them.
            var translator = implementor.CreateTranslator();

            var paths = new CosmosPath?[projects.size()];

            for (var i = 0; i < projects.size(); i++)
            {
                var node = (RexNode)projects.get(i);
                implementor.Query.SelectProperty((string)names.get(i), translator.Translate(node));

                // Null where the projection computes rather than addresses. Nothing downstream can
                // refer to it, because Cosmos has no name for it — a projection alias is not
                // addressable from ORDER BY or WHERE.
                paths[i] = translator.TryResolvePath(node, out var path) ? path : null;
            }

            // Downstream clauses address the source document, not the projected object — Cosmos
            // ORDER BY cannot reference a projection alias. Rebinding to the underlying paths is
            // therefore what lets a sort above a projection still work.
            //
            // Per ordinal, not all or nothing: a computed column bound to null declines only the
            // operators that actually read it, leaving a sort or filter over the plain paths beside
            // it still pushable.
            implementor.Fields = paths;
        }

    }

}
