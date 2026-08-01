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
        /// <param name="rowType">The input row type, consulted for the nullability of each key.</param>
        /// <param name="keys">On success, the resolved keys in order.</param>
        /// <param name="paths">On success, the resolved paths in order.</param>
        /// <returns><c>true</c> if every key resolved; otherwise <c>false</c>.</returns>
        public static bool TryResolveSortKeys(RelCollation collation, IReadOnlyList<CosmosPath> fields, org.apache.calcite.rel.type.RelDataType rowType, out IReadOnlyList<CosmosSortKey> keys, out IReadOnlyList<CosmosPath> paths)
        {
            keys = System.Array.Empty<CosmosSortKey>();
            paths = System.Array.Empty<CosmosPath>();

            if (collation is null || fields is null || rowType is null)
                return false;

            var typeFields = rowType.getFieldList();
            var collations = collation.getFieldCollations();
            var resolvedKeys = new CosmosSortKey[collations.size()];
            var resolvedPaths = new CosmosPath[collations.size()];

            for (var i = 0; i < collations.size(); i++)
            {
                var field = (RelFieldCollation)collations.get(i);
                var index = field.getFieldIndex();
                if (index < 0 || index >= fields.Count || index >= typeFields.size())
                    return false;

                var nullable = ((org.apache.calcite.rel.type.RelDataTypeField)typeFields.get(index)).getType().isNullable();

                if (TryGetDescending(field, nullable, out var descending) == false)
                    return false;

                resolvedPaths[i] = fields[index];
                resolvedKeys[i] = new CosmosSortKey(fields[index].ToPolicyPath(), descending);
            }

            keys = resolvedKeys;
            paths = resolvedPaths;
            return true;
        }

        /// <summary>
        /// Maps a field collation onto a plain ascending or descending flag, refusing any
        /// collation whose null placement Cosmos cannot honour.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Cosmos <c>ORDER BY</c> offers only <c>ASC</c> and <c>DESC</c>. Clustered collations have
        /// no equivalent and are refused.
        /// </para>
        /// <para>
        /// Cosmos also offers no control over where nulls sort. Its ordering is a total order over
        /// JSON types — <c>undefined</c> &lt; <c>null</c> &lt; boolean &lt; number &lt; string &lt;
        /// array &lt; object — and <c>DESC</c> is the exact reverse of <c>ASC</c>. Missing and null
        /// properties therefore sort below everything ascending and above everything descending,
        /// which is precisely nulls-first ascending and nulls-last descending.
        /// </para>
        /// <para>
        /// Calcite's defaults are the opposite on both counts: ascending defaults to nulls last and
        /// descending to nulls first. The placement is therefore only honourable when the key
        /// cannot be null at all, or when the plan happens to ask for Cosmos's own order. On a
        /// nullable key a conflicting placement is refused, because pushing it down would return
        /// rows in an order the plan did not ask for — a wrong answer rather than a failure.
        /// </para>
        /// <para>
        /// Verified empirically against the Cosmos emulator; see <c>DESIGN.md</c>.
        /// </para>
        /// </remarks>
        static bool TryGetDescending(RelFieldCollation field, bool nullable, out bool descending)
        {
            switch ((RelFieldCollation.Direction.__Enum)field.getDirection().ordinal())
            {
                case RelFieldCollation.Direction.__Enum.ASCENDING:
                case RelFieldCollation.Direction.__Enum.STRICTLY_ASCENDING:
                    descending = false;
                    break;
                case RelFieldCollation.Direction.__Enum.DESCENDING:
                case RelFieldCollation.Direction.__Enum.STRICTLY_DESCENDING:
                    descending = true;
                    break;
                default:
                    descending = false;
                    return false;
            }

            // A key that cannot be null has no null placement to disagree about.
            if (nullable == false)
                return true;

            switch ((RelFieldCollation.NullDirection.__Enum)field.nullDirection.ordinal())
            {
                case RelFieldCollation.NullDirection.__Enum.UNSPECIFIED:
                    return true;
                case RelFieldCollation.NullDirection.__Enum.FIRST:
                    return descending == false;
                case RelFieldCollation.NullDirection.__Enum.LAST:
                    return descending;
                default:
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

            if (TryResolveSortKeys(getCollation(), implementor.Fields, getInput().getRowType(), out var keys, out var paths) == false)
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
