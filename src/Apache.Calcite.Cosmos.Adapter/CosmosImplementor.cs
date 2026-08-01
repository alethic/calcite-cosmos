using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Metadata;
using Apache.Calcite.Cosmos.Adapter.Rel;
using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.rel;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// A rendered Cosmos SQL statement together with the values bound to its parameters.
    /// </summary>
    /// <param name="Sql">The statement text.</param>
    /// <param name="Parameters">The bound parameter values.</param>
    public readonly record struct CosmosQuery(string Sql, IReadOnlyList<CosmosParameter> Parameters);

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

        readonly RexBuilder _rexBuilder;
        readonly CosmosContainerMetadata _container;
        readonly CosmosParameterList _parameters = new();
        readonly CosmosQueryBuilder _query;

        IReadOnlyList<CosmosPath> _fields;

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
        /// <exception cref="ArgumentNullException">The value is <c>null</c>.</exception>
        public IReadOnlyList<CosmosPath> Fields
        {
            get => _fields;
            set => _fields = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Creates a translator bound to the current field bindings and parameter list.
        /// </summary>
        /// <remarks>
        /// A fresh translator is returned each time so that it always reflects the current
        /// <see cref="Fields"/>; parameters continue to accumulate into the shared list.
        /// </remarks>
        /// <returns>The translator.</returns>
        public CosmosRexTranslator CreateTranslator() => new(_rexBuilder, _fields, _parameters);

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
        public CosmosQuery Build() => new(_query.Build(), _parameters.Parameters);

    }

}
