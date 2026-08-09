using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Assembles a single Cosmos SQL statement from its clauses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder holds already-rendered expression text; it does not translate relational
    /// expressions. It exists to own clause ordering and, more importantly, to enforce the
    /// combinations Cosmos rejects. <see cref="Build"/> throws rather than emitting a statement
    /// the service would refuse.
    /// </para>
    /// <para>
    /// Cosmos has no derived tables, so a builder corresponds to exactly one statement and
    /// nothing nests. Operators that cannot be folded into these clauses are not pushed down at
    /// all.
    /// </para>
    /// </remarks>
    public sealed class CosmosQueryBuilder
    {

        readonly List<(string Alias, string Expression)> _properties = new();
        readonly List<(string Alias, string Expression)> _unnests = new();
        readonly List<string> _groupBy = new();
        readonly List<(string Expression, bool Descending)> _orderBy = new();

        string _rootAlias;
        string _container;
        string? _valueExpression;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="container">The container name to emit in the <c>FROM</c> clause.</param>
        /// <param name="rootAlias">The alias bound to the container.</param>
        /// <exception cref="ArgumentException"><paramref name="container"/> or <paramref name="rootAlias"/> is <c>null</c> or empty.</exception>
        public CosmosQueryBuilder(string container, string rootAlias)
        {
            if (string.IsNullOrEmpty(container))
                throw new ArgumentException($"'{nameof(container)}' cannot be null or empty.", nameof(container));
            if (string.IsNullOrEmpty(rootAlias))
                throw new ArgumentException($"'{nameof(rootAlias)}' cannot be null or empty.", nameof(rootAlias));

            _container = container;
            _rootAlias = rootAlias;
        }

        /// <summary>
        /// Gets the alias bound to the container in the <c>FROM</c> clause.
        /// </summary>
        public string RootAlias => _rootAlias;

        /// <summary>
        /// Gets or sets whether <c>DISTINCT</c> is applied to the projection.
        /// </summary>
        public bool Distinct { get; set; }

        /// <summary>
        /// Gets or sets the <c>TOP</c> count, or <c>null</c> for none.
        /// </summary>
        /// <remarks>
        /// Mutually exclusive with <see cref="Offset"/> and <see cref="Fetch"/>.
        /// </remarks>
        public int? Top { get; set; }

        /// <summary>
        /// Gets or sets the rendered <c>WHERE</c> predicate, or <c>null</c> for none.
        /// </summary>
        public string? Where { get; set; }

        /// <summary>
        /// Gets or sets the number of rows to skip, or <c>null</c> for none.
        /// </summary>
        /// <remarks>
        /// Cosmos spells skip and take as a single <c>OFFSET n LIMIT m</c> clause and requires
        /// both, so setting either causes both to be emitted.
        /// </remarks>
        public int? Offset { get; set; }

        /// <summary>
        /// Gets or sets the maximum number of rows to return, or <c>null</c> for none.
        /// </summary>
        public int? Fetch { get; set; }

        /// <summary>
        /// Gets or sets whether the projection is written as a flat select list rather than an
        /// object constructor.
        /// </summary>
        /// <remarks>
        /// The two are documented as equivalent — a flat list is sugar for the object form — but
        /// they are not interchangeable in practice. Cosmos rejects an aggregate inside an object
        /// constructor with <c>"Compositions of aggregates and other expressions are not
        /// allowed."</c>, so a grouped projection has to be written flat.
        /// </remarks>
        public bool FlatProjection { get; set; }

        /// <summary>
        /// Gets whether any projection has been specified.
        /// </summary>
        public bool HasProjection => _valueExpression is not null || _properties.Count > 0;

        /// <summary>
        /// Gets whether the statement carries a <c>GROUP BY</c> clause.
        /// </summary>
        public bool HasGroupBy => _groupBy.Count > 0;

        /// <summary>
        /// Gets whether the statement already restricts how many rows it returns.
        /// </summary>
        /// <remarks>
        /// Cosmos applies <c>OFFSET</c>/<c>LIMIT</c> and <c>TOP</c> last, after filtering,
        /// traversal and grouping. An operator that appears above one of those in the plan but
        /// would be written into an earlier clause therefore cannot be folded into the same
        /// statement — doing so would apply it before the restriction rather than after, and
        /// return different rows.
        /// </remarks>
        public bool HasRowLimit => Offset is not null || Fetch is not null || Top is not null;

        /// <summary>
        /// Gets whether the statement carries an <c>ORDER BY</c> clause.
        /// </summary>
        public bool HasOrderBy => _orderBy.Count > 0;

        /// <summary>
        /// Gets whether an array traversal has been added.
        /// </summary>
        public bool HasUnnest => _unnests.Count > 0;

        /// <summary>
        /// Gets the number of <c>ORDER BY</c> keys.
        /// </summary>
        public int OrderByCount => _orderBy.Count;

        /// <summary>
        /// Projects a single unwrapped value, emitting <c>SELECT VALUE &lt;expression&gt;</c>.
        /// </summary>
        /// <remarks>
        /// This is the natural projection for the single-column map row model: the result of the
        /// query is the document itself rather than an object wrapping it.
        /// </remarks>
        /// <param name="expression">The rendered expression to project.</param>
        /// <exception cref="ArgumentException"><paramref name="expression"/> is <c>null</c> or empty.</exception>
        /// <exception cref="InvalidOperationException">A property projection has already been added.</exception>
        public void SelectValue(string expression)
        {
            if (string.IsNullOrEmpty(expression))
                throw new ArgumentException($"'{nameof(expression)}' cannot be null or empty.", nameof(expression));
            if (_properties.Count > 0)
                throw new InvalidOperationException("Cannot combine a VALUE projection with property projections.");

            _valueExpression = expression;
        }

        /// <summary>
        /// Adds a named property to the projection.
        /// </summary>
        /// <remarks>
        /// Cosmos treats a flat select list as sugar for an object constructor, so the emitted
        /// form is <c>SELECT VALUE { alias: expression, … }</c>, which is equivalent and explicit.
        /// </remarks>
        /// <param name="alias">The property name in the projected object.</param>
        /// <param name="expression">The rendered expression producing the value.</param>
        /// <exception cref="ArgumentException"><paramref name="alias"/> or <paramref name="expression"/> is <c>null</c> or empty.</exception>
        /// <exception cref="InvalidOperationException">A <c>VALUE</c> projection has already been set.</exception>
        public void SelectProperty(string alias, string expression)
        {
            if (string.IsNullOrEmpty(alias))
                throw new ArgumentException($"'{nameof(alias)}' cannot be null or empty.", nameof(alias));
            if (string.IsNullOrEmpty(expression))
                throw new ArgumentException($"'{nameof(expression)}' cannot be null or empty.", nameof(expression));
            if (_valueExpression is not null)
                throw new InvalidOperationException("Cannot combine property projections with a VALUE projection.");

            _properties.Add((alias, expression));
        }

        /// <summary>
        /// Discards any projection set so far.
        /// </summary>
        /// <remarks>
        /// Calcite places a projection below an aggregate to prune columns. Cosmos has one
        /// <c>SELECT</c> per statement, so the aggregate's projection supersedes it — the pruning
        /// has no independent effect once the grouping is expressed over the same paths.
        /// </remarks>
        public void ClearProjection()
        {
            _valueExpression = null;
            _properties.Clear();
        }

        /// <summary>
        /// Adds an array traversal, emitting <c>JOIN &lt;alias&gt; IN &lt;expression&gt;</c>.
        /// </summary>
        /// <remarks>
        /// Despite the keyword, this is not a relational join: Cosmos <c>JOIN</c> cross-products a
        /// document with one of its own nested arrays. It has no join predicate.
        /// </remarks>
        /// <param name="alias">The alias bound to each array element.</param>
        /// <param name="expression">The rendered path expression producing the array.</param>
        /// <exception cref="ArgumentException"><paramref name="alias"/> or <paramref name="expression"/> is <c>null</c> or empty.</exception>
        public void AddUnnest(string alias, string expression)
        {
            if (string.IsNullOrEmpty(alias))
                throw new ArgumentException($"'{nameof(alias)}' cannot be null or empty.", nameof(alias));
            if (string.IsNullOrEmpty(expression))
                throw new ArgumentException($"'{nameof(expression)}' cannot be null or empty.", nameof(expression));

            _unnests.Add((alias, expression));
        }

        /// <summary>
        /// Adds a <c>GROUP BY</c> key.
        /// </summary>
        /// <param name="expression">The rendered grouping expression.</param>
        /// <exception cref="ArgumentException"><paramref name="expression"/> is <c>null</c> or empty.</exception>
        public void AddGroupBy(string expression)
        {
            if (string.IsNullOrEmpty(expression))
                throw new ArgumentException($"'{nameof(expression)}' cannot be null or empty.", nameof(expression));

            _groupBy.Add(expression);
        }

        /// <summary>
        /// Adds an <c>ORDER BY</c> key.
        /// </summary>
        /// <param name="expression">The rendered sort expression.</param>
        /// <param name="descending">Whether to sort descending.</param>
        /// <exception cref="ArgumentException"><paramref name="expression"/> is <c>null</c> or empty.</exception>
        public void AddOrderBy(string expression, bool descending)
        {
            if (string.IsNullOrEmpty(expression))
                throw new ArgumentException($"'{nameof(expression)}' cannot be null or empty.", nameof(expression));

            _orderBy.Add((expression, descending));
        }

        /// <summary>
        /// Gets or sets the scoring function to rank by, emitting <c>ORDER BY RANK &lt;function&gt;</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A separate clause from <see cref="AddOrderBy"/>, not a kind of sort key. It takes a scoring
        /// function — <c>FullTextScore</c>, <c>VectorDistance</c> or <c>RRF</c> over them — and has no
        /// direction, the ranking being the function's own.
        /// </para>
        /// <para>
        /// It cannot be combined with an ordinary <c>ORDER BY</c>; the reference says so of <c>RRF</c>
        /// explicitly, and there is one <c>ORDER BY</c> clause either way. <see cref="Build"/> enforces
        /// that rather than emitting both.
        /// </para>
        /// </remarks>
        public string? RankBy { get; set; }

        /// <summary>
        /// Gets whether a scoring function has been set.
        /// </summary>
        public bool HasOrderByRank => string.IsNullOrEmpty(RankBy) == false;

        /// <summary>
        /// Renders the statement.
        /// </summary>
        /// <returns>The Cosmos SQL text.</returns>
        /// <exception cref="InvalidOperationException">
        /// The accumulated clauses form a statement Cosmos does not accept: <c>GROUP BY</c>
        /// combined with <c>ORDER BY</c>, <c>TOP</c> combined with <c>OFFSET</c>/<c>LIMIT</c>, or
        /// <c>ORDER BY RANK</c> combined with an ordinary <c>ORDER BY</c> or with <c>GROUP BY</c>.
        /// </exception>
        public string Build()
        {
            // Cosmos does not support GROUP BY and ORDER BY in the same query. The conversion
            // rules are expected to prevent this pairing; reaching here means one let it through.
            if (HasGroupBy && HasOrderBy)
                throw new InvalidOperationException("Cosmos SQL does not support GROUP BY and ORDER BY in the same query.");

            // A statement has one ORDER BY clause, and ranking by a scoring function is a form of it.
            if (HasOrderByRank && HasOrderBy)
                throw new InvalidOperationException("Cosmos SQL does not support ORDER BY RANK together with an ordinary ORDER BY.");

            if (HasOrderByRank && HasGroupBy)
                throw new InvalidOperationException("Cosmos SQL does not support ORDER BY RANK together with GROUP BY.");

            if (Top is not null && (Offset is not null || Fetch is not null))
                throw new InvalidOperationException("Cosmos SQL does not support TOP together with OFFSET/LIMIT.");

            if (Top is < 0)
                throw new InvalidOperationException("TOP cannot be negative.");
            if (Offset is < 0)
                throw new InvalidOperationException("OFFSET cannot be negative.");
            if (Fetch is < 0)
                throw new InvalidOperationException("LIMIT cannot be negative.");

            var builder = new StringBuilder();

            WriteSelect(builder);
            WriteFrom(builder);
            WriteWhere(builder);
            WriteGroupBy(builder);
            WriteOrderBy(builder);
            WriteOffsetLimit(builder);

            return builder.ToString();
        }

        void WriteSelect(StringBuilder builder)
        {
            builder.Append("SELECT");

            if (Distinct)
                builder.Append(" DISTINCT");

            if (Top is int top)
                builder.Append(" TOP ").Append(top.ToString(CultureInfo.InvariantCulture));

            if (_valueExpression is not null)
            {
                builder.Append(" VALUE ").Append(_valueExpression);
            }
            else if (_properties.Count > 0 && FlatProjection)
            {
                for (var i = 0; i < _properties.Count; i++)
                {
                    var (alias, expression) = _properties[i];
                    builder.Append(i > 0 ? ", " : " ").Append(expression).Append(" AS ");
                    CosmosSql.WriteStringLiteral(builder, alias);
                }
            }
            else if (_properties.Count > 0)
            {
                // A flat select list is sugar for an object constructor; emit the explicit form.
                builder.Append(" VALUE {");

                for (var i = 0; i < _properties.Count; i++)
                {
                    if (i > 0)
                        builder.Append(',');

                    var (alias, expression) = _properties[i];
                    builder.Append(' ');
                    CosmosSql.WriteStringLiteral(builder, alias);
                    builder.Append(": ").Append(expression);
                }

                builder.Append(" }");
            }
            else
            {
                // No projection specified: return the document itself.
                builder.Append(" VALUE ").Append(_rootAlias);
            }
        }

        void WriteFrom(StringBuilder builder)
        {
            builder.Append(" FROM ").Append(_container).Append(' ').Append(_rootAlias);

            foreach (var (alias, expression) in _unnests)
                builder.Append(" JOIN ").Append(alias).Append(" IN ").Append(expression);
        }

        void WriteWhere(StringBuilder builder)
        {
            if (string.IsNullOrEmpty(Where) == false)
                builder.Append(" WHERE ").Append(Where);
        }

        void WriteGroupBy(StringBuilder builder)
        {
            if (_groupBy.Count == 0)
                return;

            builder.Append(" GROUP BY ");

            for (var i = 0; i < _groupBy.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                builder.Append(_groupBy[i]);
            }
        }

        void WriteOrderBy(StringBuilder builder)
        {
            if (HasOrderByRank)
            {
                // No direction: the scoring function defines the ranking, highest relevance first.
                builder.Append(" ORDER BY RANK ").Append(RankBy);
                return;
            }

            if (_orderBy.Count == 0)
                return;

            builder.Append(" ORDER BY ");

            for (var i = 0; i < _orderBy.Count; i++)
            {
                if (i > 0)
                    builder.Append(", ");

                var (expression, descending) = _orderBy[i];
                builder.Append(expression).Append(descending ? " DESC" : " ASC");
            }
        }

        void WriteOffsetLimit(StringBuilder builder)
        {
            if (Offset is null && Fetch is null)
                return;

            // Cosmos requires both halves of the clause even when only one is meaningful.
            builder
                .Append(" OFFSET ").Append((Offset ?? 0).ToString(CultureInfo.InvariantCulture))
                .Append(" LIMIT ").Append((Fetch ?? int.MaxValue).ToString(CultureInfo.InvariantCulture));
        }

    }

}
