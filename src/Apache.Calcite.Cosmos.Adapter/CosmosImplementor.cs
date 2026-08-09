using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A rendered Cosmos SQL statement together with the values bound to its parameters.
    /// </summary>
    /// <param name="Sql">The statement text.</param>
    /// <param name="Parameters">The bound parameter values.</param>
    /// <param name="PartitionKeyValues">
    /// The leading partition key values the predicate pinned, outermost first, or <c>null</c> where it
    /// pinned none. Supplying them restricts execution to the partitions holding that prefix instead of
    /// fanning out across every one — a complete key reaches a single logical partition, and a prefix of
    /// a hierarchical key reaches the subset under it. Either way it routes and does not filter.
    /// </param>
    /// <param name="PartitionKeyIsComplete">
    /// Whether <paramref name="PartitionKeyValues"/> covers every declared path. A prefix routes but
    /// does not identify a document, so a point read requires this and a query does not.
    /// </param>
    /// <param name="MaxItemCount">
    /// The most rows the statement can return, when the statement says so, otherwise <c>null</c>.
    /// <para>
    /// A page size rather than a limit: it caps how many rows a single round trip brings back and
    /// cannot change which rows the query yields. Without it a statement ending in <c>LIMIT 5</c>
    /// still fetches a full default page, and pays for the rows it then discards.
    /// </para>
    /// </param>
    /// <param name="PointReadId">
    /// The <c>id</c> to read directly when the statement is exactly a lookup by <c>id</c> and a
    /// complete partition key, otherwise <c>null</c>.
    /// <para>
    /// A point read costs about 1 RU where the same fetch as a query costs 2.3 at best and goes through
    /// the query engine. It applies no predicate and returns the document rather than a projection, so
    /// it is offered only where the statement asks for nothing a read cannot answer — see
    /// <see cref="CosmosImplementor.Build"/> for what that excludes, and
    /// <c>CosmosPartitionKeyExtractor.TryExtractPointRead</c> for the predicate condition.
    /// </para>
    /// </param>
    public readonly record struct CosmosQuery(string Sql, IReadOnlyList<CosmosParameter> Parameters, IReadOnlyList<object?>? PartitionKeyValues = null, int? MaxItemCount = null, string? PointReadId = null, bool PartitionKeyIsComplete = false);

    /// <summary>
    /// Accumulates the state contributed by a tree of <see cref="CosmosRel"/> nodes and renders
    /// the resulting Cosmos SQL statement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nodes do not return SQL fragments. Each contributes to the implementor as it is visited,
    /// and the statement is rendered once the whole subtree has been consumed. Because Cosmos has
    /// no derived tables, one implementor corresponds to exactly one statement and nothing nests.
    /// </para>
    /// <para>
    /// <see cref="Fields"/> is the binding from input field ordinal to document path for the node
    /// currently being visited. A node that changes the shape of its output — a projection —
    /// replaces it before returning.
    /// </para>
    /// </remarks>
    public sealed class CosmosImplementor
    {

        /// <summary>
        /// The alias bound to the container when the caller does not specify one.
        /// </summary>
        public const string DefaultRootAlias = "c";

        /// <summary>
        /// The name of the column carrying the whole document in the map row model.
        /// </summary>
        public const string MapColumnName = "_MAP";

        /// <summary>
        /// Derives the ordinal-to-path binding for a row type.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The map column binds to the document root; every other field is a promoted column and
        /// binds to the property of the same name. Nested promoted paths are not expressible this
        /// way and are not currently produced.
        /// </para>
        /// <para>
        /// This is deliberately a pure function of the row type, so that conversion rules can
        /// compute the binding before a node exists and decide whether an expression is
        /// translatable — the alternative being to convert optimistically and fail later.
        /// </para>
        /// </remarks>
        /// <param name="rowType">The row type to bind.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <returns>The binding, indexed by field ordinal.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="rowType"/> is <c>null</c>.</exception>
        public static IReadOnlyList<CosmosPath?> BindFields(org.apache.calcite.rel.type.RelDataType rowType, string? rootAlias = null)
        {
            if (rowType is null)
                throw new ArgumentNullException(nameof(rowType));

            var root = CosmosPath.Root(string.IsNullOrEmpty(rootAlias) ? DefaultRootAlias : rootAlias);
            var fields = rowType.getFieldList();
            var paths = new CosmosPath[fields.size()];

            for (var i = 0; i < paths.Length; i++)
            {
                var name = ((org.apache.calcite.rel.type.RelDataTypeField)fields.get(i)).getName();
                paths[i] = string.Equals(name, MapColumnName, StringComparison.Ordinal) ? root : root.Property(name);
            }

            return paths;
        }

        /// <summary>
        /// Derives the binding a node's <em>output</em> carries, by walking the subtree the way
        /// implementation will.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="BindFields"/> answers from a row type alone, which is right only where the field
        /// names are document properties — a scan, or anything above one that preserves the row type. It
        /// is wrong above a projection, whose names are aliases: it invents <c>c.u</c> for a column
        /// named <c>u</c>, and a rule deciding on invented paths can fire on a projection it cannot
        /// render, or check a composite index against paths the container does not have.
        /// </para>
        /// <para>
        /// This walks instead, and mirrors each node's own <c>Implement</c>: a scan binds its row type,
        /// a filter and a sort pass the binding through, a projection rebinds to the paths its
        /// expressions resolve to — <c>null</c> where one computes rather than addresses — and an array
        /// traversal appends an unbound column for the element, which is what makes a sort on it decline
        /// here exactly as it does there. A node it does not know returns <c>false</c>, and the caller
        /// declines rather than guessing.
        /// </para>
        /// </remarks>
        /// <param name="node">The node whose output binding is wanted.</param>
        /// <param name="fields">On success, the binding indexed by field ordinal.</param>
        /// <returns><c>true</c> if the binding could be derived; otherwise <c>false</c>.</returns>
        public static bool TryBindOutput(RelNode? node, out IReadOnlyList<CosmosPath?> fields)
        {
            fields = Array.Empty<CosmosPath?>();

            // In a Volcano plan an input is a set of equivalent expressions rather than one node. Any
            // member binds the same way — they are equivalent — so the one the set was built from will
            // do, and a set with none is not something to guess about.
            if (node is org.apache.calcite.plan.volcano.RelSubset subset)
                node = subset.getOriginal() ?? subset.getBest();

            switch (node)
            {
                case null:
                    return false;

                case TableScan scan when scan.getTable()?.unwrap(typeof(CosmosTable)) is CosmosTable:
                    fields = BindFields(scan.getRowType());
                    return true;

                // Neither changes the shape of a row, so neither changes what addresses it.
                case Filter filter:
                    return TryBindOutput(filter.getInput(), out fields);
                case Sort sort:
                    return TryBindOutput(sort.getInput(), out fields);

                case Project project:
                {
                    if (TryBindOutput(project.getInput(), out var input) == false)
                        return false;

                    var translator = new CosmosRexTranslator(project.getCluster().getRexBuilder(), input, new CosmosParameterList());
                    var projects = project.getProjects();
                    var paths = new CosmosPath?[projects.size()];

                    for (var i = 0; i < paths.Length; i++)
                        paths[i] = translator.TryResolvePath((RexNode)projects.get(i), out var path) ? path : null;

                    fields = paths;
                    return true;
                }

                // An array traversal is a correlated join whose right side contributes the element. The
                // element has no container-rooted path, and the service will not order by one, so it is
                // left unbound rather than named.
                case Correlate correlate:
                {
                    if (TryBindOutput(correlate.getLeft(), out var left) == false)
                        return false;

                    var paths = new CosmosPath?[correlate.getRowType().getFieldCount()];
                    for (var i = 0; i < paths.Length; i++)
                        paths[i] = i < left.Count ? left[i] : null;

                    fields = paths;
                    return true;
                }

                default:
                    return false;
            }
        }

        readonly RexBuilder _rexBuilder;
        readonly CosmosContainerMetadata _container;
        readonly CosmosParameterList _parameters = new();
        readonly CosmosQueryBuilder _query;

        IReadOnlyList<CosmosPath?> _fields;
        int _unnestAliases;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="rexBuilder">Used when translating expressions.</param>
        /// <param name="container">The container being queried.</param>
        /// <param name="rootAlias">The alias to bind the container to, or <c>null</c> for <see cref="DefaultRootAlias"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="rexBuilder"/> or <paramref name="container"/> is <c>null</c>.</exception>
        public CosmosImplementor(RexBuilder rexBuilder, CosmosContainerMetadata container, string? rootAlias = null)
        {
            _rexBuilder = rexBuilder ?? throw new ArgumentNullException(nameof(rexBuilder));
            _container = container ?? throw new ArgumentNullException(nameof(container));

            var alias = string.IsNullOrEmpty(rootAlias) ? DefaultRootAlias : rootAlias;
            _query = new CosmosQueryBuilder(container.Name, alias);

            // Until a scan binds the row type, the sole field is the document itself. This is the
            // binding the single-column map row model starts from.
            _fields = new[] { CosmosPath.Root(alias) };
        }

        /// <summary>
        /// Gets the container being queried.
        /// </summary>
        public CosmosContainerMetadata Container => _container;

        /// <summary>
        /// Gets the statement under construction.
        /// </summary>
        public CosmosQueryBuilder Query => _query;

        /// <summary>
        /// Gets the parameters bound so far.
        /// </summary>
        public CosmosParameterList Parameters => _parameters;

        /// <summary>
        /// Gets the alias bound to the container.
        /// </summary>
        public string RootAlias => _query.RootAlias;

        /// <summary>
        /// Gets a path rooted at the container alias.
        /// </summary>
        public CosmosPath Root => CosmosPath.Root(_query.RootAlias);

        /// <summary>
        /// Gets or sets the binding from input field ordinal to document path.
        /// </summary>
        /// <remarks>
        /// An entry is <c>null</c> where the field has no document path — a projection of a computed
        /// expression, which addresses nothing Cosmos can name. That is per-ordinal rather than
        /// per-binding: one computed column does not stop a downstream operator referring to the plain
        /// paths beside it, and the operator declines only if it actually reads the computed one.
        /// </remarks>
        /// <exception cref="ArgumentNullException">The value is <c>null</c>.</exception>
        public IReadOnlyList<CosmosPath?> Fields
        {
            get => _fields;
            set => _fields = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Gets or sets the partition key values a filter pinned, or <c>null</c> if none did.
        /// </summary>
        /// <remarks>
        /// Set by the first filter that pins every declared partition key path. It affects how the
        /// statement is executed, not what it says.
        /// </remarks>
        public IReadOnlyList<object?>? PartitionKeyValues { get; set; }

        /// <summary>
        /// Gets or sets whether <see cref="PartitionKeyValues"/> covers every declared path.
        /// </summary>
        public bool PartitionKeyIsComplete { get; set; }

        /// <summary>
        /// Allocates a fresh alias for an array traversal.
        /// </summary>
        /// <remarks>
        /// Cosmos requires every alias in a <c>FROM</c> clause to be unique, and a query may
        /// traverse several arrays.
        /// </remarks>
        /// <returns>The alias.</returns>
        public string CreateUnnestAlias() => "t" + (_unnestAliases++).ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// Creates a translator bound to the current field bindings and parameter list.
        /// </summary>
        /// <remarks>
        /// A fresh translator is returned each time so that it always reflects the current
        /// <see cref="Fields"/>; parameters continue to accumulate into the shared list.
        /// </remarks>
        /// <returns>The translator.</returns>
        public CosmosRexTranslator CreateTranslator(org.apache.calcite.rel.core.CorrelationId? ownRow = null) => new(_rexBuilder, _fields, _parameters, ownRow);

        /// <summary>
        /// Visits an input node, allowing it to contribute to this implementor.
        /// </summary>
        /// <param name="input">The node to visit.</param>
        /// <exception cref="ArgumentNullException"><paramref name="input"/> is <c>null</c>.</exception>
        /// <exception cref="CosmosTranslationException"><paramref name="input"/> is not in the Cosmos convention.</exception>
        public void Visit(RelNode input)
        {
            if (input is null)
                throw new ArgumentNullException(nameof(input));

            if (input is not CosmosRel rel)
                throw new CosmosTranslationException($"Node '{input.getRelTypeName()}' is not in the Cosmos convention.");

            rel.Implement(this);
        }

        /// <summary>
        /// Translates an expression against the current field bindings.
        /// </summary>
        /// <param name="node">The expression to translate.</param>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="CosmosTranslationException">The expression has no Cosmos equivalent.</exception>
        public string Translate(RexNode node) => CreateTranslator().Translate(node);

        /// <summary>
        /// Renders the accumulated statement.
        /// </summary>
        /// <returns>The statement text and its bound parameters.</returns>
        /// <exception cref="InvalidOperationException">The accumulated clauses form a statement Cosmos does not accept.</exception>
        public CosmosQuery Build() => new(_query.Build(), _parameters.Parameters, PartitionKeyValues, MaxItemCount(), PointReadId(), PartitionKeyIsComplete);

        /// <summary>
        /// Gets or sets the <c>id</c> a filter pinned alongside a complete partition key, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Set by a filter whose predicate says nothing beyond those equalities. Whether it is acted on
        /// is <see cref="Build"/>'s decision, because the predicate is only half the question.
        /// </remarks>
        public string? PointReadCandidate { get; set; }

        /// <summary>
        /// Returns the <c>id</c> to read directly, or <c>null</c> where the statement asks for something
        /// a point read cannot answer.
        /// </summary>
        /// <remarks>
        /// A point read returns one document, whole and unfiltered. So every clause beyond the pinned
        /// predicate rules it out: a projection is computed by the query engine, a row limit and an
        /// ordering describe a result set rather than a document, a grouping aggregates one, and an
        /// array traversal returns a row per element rather than a row per document. The projection is
        /// the exception — the converter can read paths out of the document itself — and it is checked
        /// there rather than here, because only the converter knows whether every output field is a path.
        /// </remarks>
        string? PointReadId()
        {
            if (PointReadCandidate is null)
                return null;

            if (_query.HasGroupBy || _query.HasOrderBy || _query.HasOrderByRank || _query.HasRowLimit || _query.HasUnnest)
                return null;

            return PointReadCandidate;
        }

        /// <summary>
        /// Returns the most rows the statement can return, or <c>null</c> where it is unbounded.
        /// </summary>
        /// <remarks>
        /// <c>OFFSET n LIMIT m</c> yields at most <c>m</c> rows, but the service must still walk the
        /// <c>n</c> it skips, so the page worth asking for is <c>n + m</c>. <c>TOP n</c> is <c>n</c>.
        /// An aggregation collapses its input and is left alone: the row it returns is not one of the
        /// rows the limit counted, and the limit cannot appear above it anyway.
        /// </remarks>
        int? MaxItemCount()
        {
            if (_query.Top is int top)
                return top;

            if (_query.Fetch is not int fetch)
                return null;

            var offset = _query.Offset ?? 0;

            // A page size has to be positive; a zero-row limit is expressed by the statement itself.
            var total = (long)offset + fetch;
            return total > 0 && total <= int.MaxValue ? (int)total : null;
        }

    }

}
