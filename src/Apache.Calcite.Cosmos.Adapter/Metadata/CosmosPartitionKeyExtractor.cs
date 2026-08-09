using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.rex;
using org.apache.calcite.sql;

namespace Apache.Calcite.Cosmos.Adapter.Metadata
{

    /// <summary>
    /// Recovers the partition key value a predicate pins, when it pins one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A query that names its partition key runs against a single physical partition; without one
    /// the service fans the query out across every partition and merges the results. This is
    /// usually the largest single difference in cost, so it is worth recovering even though it
    /// changes nothing about the statement itself.
    /// </para>
    /// <para>
    /// Only a conjunction of equalities against constants qualifies. A disjunction may match
    /// several partitions and a range predicate says nothing about which — in either case the
    /// query must fan out, and claiming otherwise would silently lose rows.
    /// </para>
    /// </remarks>
    public static class CosmosPartitionKeyExtractor
    {

        /// <summary>
        /// Attempts to recover a complete partition key from a predicate.
        /// </summary>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="container">The container whose partition key is sought.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, one value per declared partition key path, in order.</param>
        /// <returns><c>true</c> if every partition key path was pinned; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
        public static bool TryExtract(RexNode condition, IReadOnlyList<CosmosPath?> fields, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values)
        {
            if (condition is null)
                throw new ArgumentNullException(nameof(condition));
            if (fields is null)
                throw new ArgumentNullException(nameof(fields));
            if (container is null)
                throw new ArgumentNullException(nameof(container));

            values = Array.Empty<object?>();

            // A container always declares a partition key; without metadata for it there is
            // nothing to pin.
            if (container.PartitionKeyPaths.Count == 0)
                return false;

            var pinned = new Dictionary<string, object?>(StringComparer.Ordinal);
            Collect(condition, fields, rootAlias, pinned);

            var resolved = new object?[container.PartitionKeyPaths.Count];

            for (var i = 0; i < container.PartitionKeyPaths.Count; i++)
            {
                if (pinned.TryGetValue(container.PartitionKeyPaths[i], out var value) == false)
                    return false;

                resolved[i] = value;
            }

            values = resolved;
            return true;
        }

        /// <summary>
        /// Walks a conjunction, recording each path pinned to a constant.
        /// </summary>
        /// <remarks>
        /// Only <c>AND</c> is descended into. Under a disjunction an equality does not constrain
        /// the whole predicate, so treating it as pinning would be wrong.
        /// </remarks>
        static void Collect(RexNode node, IReadOnlyList<CosmosPath?> fields, string rootAlias, Dictionary<string, object?> pinned)
        {
            if (node is not RexCall call)
                return;

            var kind = (SqlKind.__Enum)call.getKind().ordinal();

            if (kind == SqlKind.__Enum.AND)
            {
                for (var i = 0; i < call.getOperands().size(); i++)
                    Collect((RexNode)call.getOperands().get(i), fields, rootAlias, pinned);

                return;
            }

            if (kind != SqlKind.__Enum.EQUALS || call.getOperands().size() != 2)
                return;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            if (TryPin(left, right, fields, rootAlias, pinned))
                return;

            TryPin(right, left, fields, rootAlias, pinned);
        }

        /// <summary>
        /// Records <paramref name="pathNode"/> as pinned when it is a container-rooted path and
        /// <paramref name="valueNode"/> is a constant.
        /// </summary>
        static bool TryPin(RexNode pathNode, RexNode valueNode, IReadOnlyList<CosmosPath?> fields, string rootAlias, Dictionary<string, object?> pinned)
        {
            if (valueNode is not RexLiteral literal)
                return false;

            // Resolution reuses the translator so that the accepted path forms are the same ones
            // the emitted statement would address. Parameters are discarded; only the shape matters.
            var translator = new CosmosRexTranslator(RexBuilderHolder.Value, fields, new CosmosParameterList());
            if (translator.TryResolvePath(pathNode, out var path) == false || path is null)
                return false;

            // A path rooted at an array-traversal alias addresses an element, not the document.
            if (string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return false;

            object? value;
            try
            {
                value = CosmosRexTranslator.GetLiteralValue(literal);
            }
            catch (CosmosTranslationException)
            {
                return false;
            }

            pinned[path.ToPolicyPath()] = value;
            return true;
        }

        /// <summary>
        /// A builder used only for path resolution, which never constructs nodes.
        /// </summary>
        static class RexBuilderHolder
        {

            internal static readonly RexBuilder Value = new(new org.apache.calcite.jdbc.JavaTypeFactoryImpl());

        }

    }

}
