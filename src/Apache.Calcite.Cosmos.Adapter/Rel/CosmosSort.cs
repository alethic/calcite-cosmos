using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Sort implemented in the <see cref="CosmosConvention"/> calling convention, rendered as an
    /// <c>ORDER BY</c> clause together with <c>OFFSET</c>/<c>LIMIT</c>.
    /// </summary>
    public class CosmosSort : Sort, CosmosRel
    {

        /// <summary>
        /// Resolves a collation into sort keys expressed as policy-form paths.
        /// </summary>
        /// <remarks>
        /// Every key must denote a path: <c>ORDER BY</c> legality is decided against the
        /// container's composite indexes, which are declared over paths. A collation over a
        /// computed expression cannot be checked and so cannot be pushed down.
        /// </remarks>
        /// <param name="collation">The requested collation.</param>
        /// <param name="fields">The ordinal-to-path binding of the input.</param>
        /// <param name="keys">On success, the resolved keys in order.</param>
        /// <param name="paths">On success, the resolved paths in order.</param>
        /// <returns><c>true</c> if every key resolved; otherwise <c>false</c>.</returns>
        public static bool TryResolveSortKeys(RelCollation collation, IReadOnlyList<CosmosPath> fields, out IReadOnlyList<CosmosSortKey> keys, out IReadOnlyList<CosmosPath> paths)
        {
            keys = System.Array.Empty<CosmosSortKey>();
            paths = System.Array.Empty<CosmosPath>();

            if (collation is null || fields is null)
                return false;

            var collations = collation.getFieldCollations();
            var resolvedKeys = new CosmosSortKey[collations.size()];
            var resolvedPaths = new CosmosPath[collations.size()];

            for (var i = 0; i < collations.size(); i++)
            {
                var field = (RelFieldCollation)collations.get(i);
                var index = field.getFieldIndex();
                if (index < 0 || index >= fields.Count)
                    return false;

                if (TryGetDescending(field, out var descending) == false)
                    return false;

                resolvedPaths[i] = fields[index];
                resolvedKeys[i] = new CosmosSortKey(fields[index].ToPolicyPath(), descending);
            }

            keys = resolvedKeys;
            paths = resolvedPaths;
            return true;
        }

        /// <summary>
        /// Maps a field collation onto a plain ascending or descending flag.
        /// </summary>
        /// <remarks>
        /// Cosmos <c>ORDER BY</c> offers only <c>ASC</c> and <c>DESC</c>. Clustered collations
        /// have no equivalent and are refused.
        /// </remarks>
        static bool TryGetDescending(RelFieldCollation field, out bool descending)
        {
            switch ((RelFieldCollation.Direction.__Enum)field.getDirection().ordinal())
            {
                case RelFieldCollation.Direction.__Enum.ASCENDING:
                case RelFieldCollation.Direction.__Enum.STRICTLY_ASCENDING:
                    descending = false;
                    return true;
                case RelFieldCollation.Direction.__Enum.DESCENDING:
                case RelFieldCollation.Direction.__Enum.STRICTLY_DESCENDING:
                    descending = true;
                    return true;
                default:
                    descending = false;
                    return false;
            }
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="collation">The requested collation.</param>
        /// <param name="offset">The number of rows to skip, or <c>null</c>.</param>
        /// <param name="fetch">The maximum number of rows to return, or <c>null</c>.</param>
        public CosmosSort(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, RelCollation collation, RexNode? offset, RexNode? fetch) :
            base(cluster, traitSet, input, collation, offset, fetch)
        {

        }

        /// <inheritdoc />
        public override Sort copy(RelTraitSet traitSet, RelNode newInput, RelCollation newCollation, RexNode? offset, RexNode? fetch)
        {
            return new CosmosSort(getCluster(), traitSet, newInput, newCollation, offset, fetch);
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

            if (implementor.Query.HasOrderBy)
                throw new CosmosTranslationException("A sort has already been applied.");

            // Cosmos rejects GROUP BY and ORDER BY in the same statement.
            if (implementor.Query.HasGroupBy)
                throw new CosmosTranslationException("Cosmos SQL does not support ORDER BY together with GROUP BY.");

            if (TryResolveSortKeys(getCollation(), implementor.Fields, out var keys, out var paths) == false)
                throw new CosmosTranslationException("The sort keys do not resolve to document paths.");

            if (implementor.Container.IsSortSupported(keys) == false)
                throw new CosmosTranslationException("The container has no composite index supporting this sort.");

            for (var i = 0; i < keys.Count; i++)
                implementor.Query.AddOrderBy(paths[i].ToString(), keys[i].Descending);

            if (offset is not null)
                implementor.Query.Offset = RexLiteral.intValue(offset);
            if (fetch is not null)
                implementor.Query.Fetch = RexLiteral.intValue(fetch);
        }

    }

}
