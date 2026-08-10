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
        /// Recovers as much of a hierarchical partition key as a predicate pins, outermost first.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A hierarchical key is up to three paths, and Cosmos routes on any <em>prefix</em> of them: a
        /// query pinning <c>/tenant</c> out of <c>/tenant, /user</c> reaches the subset of physical
        /// partitions holding that tenant rather than every partition in the container. That is a large
        /// saving on a container with many partitions, and <see cref="TryExtract"/> — which answers only
        /// for a complete key — throws it away.
        /// </para>
        /// <para>
        /// <b>Prefix means prefix.</b> Pinning <c>/user</c> without <c>/tenant</c> narrows nothing,
        /// because the routing is on the leading components; so this takes the longest run of pinned
        /// paths starting at the outermost and stops at the first gap. Supplying a prefix restricts
        /// which partitions are visited and filters no rows, so the predicate is unaffected either way.
        /// </para>
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="container">The container whose partition key is sought.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, the pinned leading values, outermost first.</param>
        /// <param name="complete">On success, whether every declared path was pinned.</param>
        /// <returns><c>true</c> if at least the outermost path was pinned; otherwise <c>false</c>.</returns>
        public static bool TryExtractPrefix(RexNode condition, IReadOnlyList<CosmosPath?> fields, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values, out bool complete)
        {
            values = Array.Empty<object?>();
            complete = false;

            if (condition is null || fields is null || container is null || container.PartitionKeyPaths.Count == 0)
                return false;

            var pinned = new Dictionary<string, object?>(StringComparer.Ordinal);
            Collect(condition, fields, rootAlias, pinned);

            var prefix = new List<object?>();

            foreach (var path in container.PartitionKeyPaths)
            {
                if (pinned.TryGetValue(path, out var value) == false)
                    break;

                prefix.Add(value);
            }

            if (prefix.Count == 0)
                return false;

            values = prefix;
            complete = prefix.Count == container.PartitionKeyPaths.Count;
            return true;
        }

        /// <summary>
        /// Attempts to recover an <c>id</c> and a complete partition key from a predicate that says
        /// nothing else — the shape a point read can answer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A point read costs about 1 RU where the same fetch as a query costs 2.3 at best and goes
        /// through the query engine. It is also blind: it takes an <c>id</c> and a partition key and
        /// returns that document, applying no predicate of its own.
        /// </para>
        /// <para>
        /// <b>That is why the predicate must say nothing else.</b> Under
        /// <c>WHERE id = 'x' AND pk = 'y' AND price &gt; 100</c> a point read would return a document
        /// the query excludes — a wrong answer rather than a slow one. So every top-level conjunct has
        /// to be one of the equalities that pins <c>id</c> or a partition key path, and all of them have
        /// to be pinned. A residual predicate is left to the query path rather than applied afterwards;
        /// reading then filtering is a different trade and worth making deliberately.
        /// </para>
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="container">The container being read.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, one value per declared partition key path, in order.</param>
        /// <param name="id">On success, the pinned <c>id</c>.</param>
        /// <returns><c>true</c> if the predicate is exactly an <c>id</c> and a complete partition key.</returns>
        public static bool TryExtractPointRead(RexNode condition, IReadOnlyList<CosmosPath?> fields, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values, out string? id)
        {
            values = Array.Empty<object?>();
            id = null;

            if (condition is null || fields is null || container is null)
                return false;

            if (TryExtract(condition, fields, container, rootAlias, out var partitionKey) == false)
                return false;

            var pinned = new Dictionary<string, object?>(StringComparer.Ordinal);
            Collect(condition, fields, rootAlias, pinned);

            // Cosmos types id as a string. Anything else pinned to it is a predicate that matches
            // nothing, and is not something to turn into a read.
            if (pinned.TryGetValue("/" + CosmosContainerMetadata.IdPropertyName, out var value) == false || value is not string pinnedId)
                return false;

            // The paths a point read accounts for: id, and the partition key. Every conjunct must be
            // one of them, or the read answers a different question than the query asked.
            var accounted = new HashSet<string>(container.PartitionKeyPaths, StringComparer.Ordinal)
            {
                "/" + CosmosContainerMetadata.IdPropertyName,
            };

            if (CoversExactly(condition, fields, rootAlias, accounted) == false)
                return false;

            values = partitionKey;
            id = pinnedId;
            return true;
        }

        /// <summary>
        /// Attempts to recover a set of <c>id</c>s and a complete partition key from a predicate that
        /// says nothing else — the shape a batch of point reads can answer.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>WHERE pk = 'x' AND id IN ('a', 'b', 'c')</c> is <see cref="TryExtractPointRead"/>'s
        /// question asked for several documents at once, and <c>ReadManyItemsAsync</c> answers it
        /// charged as point reads. The same blindness applies: the batch read applies no predicate,
        /// so every conjunct must be a partition key equality or the one disjunction of <c>id</c>
        /// equalities, and anything else leaves the statement to the query path.
        /// </para>
        /// <para>
        /// An <c>IN</c> arrives as a <c>SEARCH</c> over a set and is expanded here the way
        /// translation expands it, so the shapes this walks are the ones the statement would carry.
        /// Duplicate <c>id</c>s collapse: <c>IN ('a', 'a')</c> asks for one document.
        /// </para>
        /// </remarks>
        /// <param name="condition">The predicate, expressed over <paramref name="fields"/>.</param>
        /// <param name="fields">The ordinal-to-path binding of the filtered input.</param>
        /// <param name="container">The container being read.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <param name="values">On success, one value per declared partition key path, in order.</param>
        /// <param name="ids">On success, the distinct pinned <c>id</c>s, in predicate order.</param>
        /// <returns><c>true</c> if the predicate is exactly a set of <c>id</c>s and a complete partition key.</returns>
        public static bool TryExtractPointReadSet(RexNode condition, IReadOnlyList<CosmosPath?> fields, CosmosContainerMetadata container, string rootAlias, out IReadOnlyList<object?> values, out IReadOnlyList<string> ids)
        {
            values = Array.Empty<object?>();
            ids = Array.Empty<string>();

            if (condition is null || fields is null || container is null)
                return false;

            condition = RexUtil.expandSearch(RexBuilderHolder.Value, null, condition);

            if (TryExtract(condition, fields, container, rootAlias, out var partitionKey) == false)
                return false;

            var conjuncts = new List<RexNode>();
            Flatten(condition, conjuncts);

            var accounted = new HashSet<string>(container.PartitionKeyPaths, StringComparer.Ordinal);
            List<string>? set = null;

            foreach (var conjunct in conjuncts)
            {
                if (conjunct is not RexCall call)
                    return false;

                if ((SqlKind.__Enum)call.getKind().ordinal() == SqlKind.__Enum.EQUALS)
                {
                    // A partition key equality accounts for itself; any other equality — an id, or
                    // something else entirely — is a shape this recovery does not answer for. A lone
                    // id equality is TryExtractPointRead's question, asked first by the caller.
                    var pinned = new Dictionary<string, object?>(StringComparer.Ordinal);
                    Collect(call, fields, rootAlias, pinned);

                    if (pinned.Count == 1 && accounted.Contains(System.Linq.Enumerable.First(pinned.Keys)))
                        continue;

                    return false;
                }

                if (TryExtractIdSet(call, fields, rootAlias, out var branchIds))
                {
                    // Two id sets in one conjunction intersect, which this does not compute.
                    if (set is not null)
                        return false;

                    set = branchIds;
                    continue;
                }

                return false;
            }

            if (set is null)
                return false;

            values = partitionKey;
            ids = set;
            return true;
        }

        /// <summary>
        /// Recognises a disjunction in which every branch pins <c>id</c> to a string.
        /// </summary>
        static bool TryExtractIdSet(RexCall call, IReadOnlyList<CosmosPath?> fields, string rootAlias, out List<string> ids)
        {
            ids = new List<string>();

            if ((SqlKind.__Enum)call.getKind().ordinal() != SqlKind.__Enum.OR)
                return false;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < call.getOperands().size(); i++)
            {
                if (call.getOperands().get(i) is not RexCall branch ||
                    (SqlKind.__Enum)branch.getKind().ordinal() != SqlKind.__Enum.EQUALS)
                    return false;

                var pinned = new Dictionary<string, object?>(StringComparer.Ordinal);
                Collect(branch, fields, rootAlias, pinned);

                // Cosmos types id as a string; a branch pinning anything else, or anything more,
                // makes the disjunction a predicate rather than a set of documents.
                if (pinned.Count != 1 ||
                    pinned.TryGetValue("/" + CosmosContainerMetadata.IdPropertyName, out var value) == false ||
                    value is not string id)
                    return false;

                if (seen.Add(id))
                    ids.Add(id);
            }

            return ids.Count > 0;
        }

        /// <summary>
        /// Flattens nested conjunctions into their top-level conjuncts.
        /// </summary>
        static void Flatten(RexNode node, List<RexNode> conjuncts)
        {
            if (node is RexCall call && (SqlKind.__Enum)call.getKind().ordinal() == SqlKind.__Enum.AND)
            {
                for (var i = 0; i < call.getOperands().size(); i++)
                    Flatten((RexNode)call.getOperands().get(i), conjuncts);

                return;
            }

            conjuncts.Add(node);
        }

        /// <summary>
        /// Determines whether every top-level conjunct is an equality pinning one of the given paths.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="Collect"/>, which records what a predicate pins and ignores
        /// the rest. This asks the question the other way round — whether there <em>is</em> a rest —
        /// because a point read applies no predicate and so cannot carry one.
        /// </remarks>
        static bool CoversExactly(RexNode node, IReadOnlyList<CosmosPath?> fields, string rootAlias, HashSet<string> accounted)
        {
            if (node is not RexCall call)
                return false;

            var kind = (SqlKind.__Enum)call.getKind().ordinal();

            if (kind == SqlKind.__Enum.AND)
            {
                for (var i = 0; i < call.getOperands().size(); i++)
                    if (CoversExactly((RexNode)call.getOperands().get(i), fields, rootAlias, accounted) == false)
                        return false;

                return true;
            }

            if (kind != SqlKind.__Enum.EQUALS || call.getOperands().size() != 2)
                return false;

            var pinned = new Dictionary<string, object?>(StringComparer.Ordinal);
            Collect(call, fields, rootAlias, pinned);

            // Exactly one path, and one this read accounts for. A conjunct pinning something else is
            // a predicate the read would ignore.
            return pinned.Count == 1 && accounted.Contains(System.Linq.Enumerable.First(pinned.Keys));
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
