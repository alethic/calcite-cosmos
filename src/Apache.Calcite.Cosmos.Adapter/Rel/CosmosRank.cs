using System;

using Apache.Calcite.Cosmos.Adapter.Sql;

using com.google.common.collect;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// A projection ranked by a scoring function, implemented in the <see cref="CosmosConvention"/>
    /// calling convention as <c>ORDER BY RANK</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One node for what Calcite expresses as three, and it has to be, because the middle of those three
    /// is illegal in Cosmos. Ordering by an expression that is not in the select list makes Calcite
    /// project the expression, sort on that column, and project it away again — and the reference is
    /// explicit that <c>FULLTEXTSCORE</c> and <c>RRF</c> <b>cannot be part of a projection</b>. So the
    /// score can never become a column, and a node that ranks has to subsume the projection rather than
    /// sit above one.
    /// </para>
    /// <para>
    /// What it carries is therefore the output projection <em>and</em> the ranking expression: the score
    /// is rendered into the <c>ORDER BY RANK</c> clause and nowhere else, and the projected columns are
    /// the ones the query actually asked for.
    /// </para>
    /// <para>
    /// A row limit belongs here too. Every documented example of the clause carries <c>TOP</c> or
    /// <c>OFFSET LIMIT</c> — ranking the whole container and returning it is not the point of ranking —
    /// and <see cref="CosmosQueryBuilder"/> refuses to combine the clause with an ordinary
    /// <c>ORDER BY</c> or with <c>GROUP BY</c>.
    /// </para>
    /// </remarks>
    public class CosmosRank : SingleRel, CosmosRel
    {

        readonly java.util.List _projects;
        readonly RexNode _rank;
        readonly RexNode? _fetch;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="projects">The projected expressions, over the input's row type.</param>
        /// <param name="rowType">The output row type.</param>
        /// <param name="rank">The scoring function to rank by, over the input's row type.</param>
        /// <param name="fetch">The row limit, or <c>null</c> for none.</param>
        public CosmosRank(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, java.util.List projects, RelDataType rowType, RexNode rank, RexNode? fetch) :
            base(cluster, traitSet, input)
        {
            _projects = projects ?? throw new ArgumentNullException(nameof(projects));
            _rank = rank ?? throw new ArgumentNullException(nameof(rank));
            _fetch = fetch;

            this.rowType = rowType ?? throw new ArgumentNullException(nameof(rowType));
        }

        /// <summary>
        /// Gets the projected expressions.
        /// </summary>
        public java.util.List Projects => _projects;

        /// <summary>
        /// Gets the scoring function rows are ranked by.
        /// </summary>
        public RexNode Rank => _rank;

        /// <summary>
        /// Gets the row limit, or <c>null</c>.
        /// </summary>
        public RexNode? Fetch => _fetch;

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosRank(getCluster(), traitSet, (RelNode)sole(inputs), _projects, getRowType(), _rank, _fetch);
        }

        /// <inheritdoc />
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            return base.computeSelfCost(planner, mq)?.multiplyBy(CosmosConvention.CostMultiplier);
        }

        /// <inheritdoc />
        public override RelWriter explainTerms(RelWriter pw)
        {
            return base.explainTerms(pw)
                .item("projects", _projects)
                .item("rank", _rank)
                .itemIf("fetch", _fetch, _fetch is not null);
        }

        /// <inheritdoc />
        public void Implement(CosmosImplementor implementor)
        {
            implementor.Visit(getInput());

            if (implementor.Query.HasProjection)
                throw new CosmosTranslationException("A projection has already been applied.");

            // The clause is one ORDER BY, so anything that already ordered or grouped rules it out. The
            // builder enforces this too; refusing here keeps the failure at the node that caused it.
            if (implementor.Query.HasOrderBy || implementor.Query.HasGroupBy)
                throw new CosmosTranslationException("ORDER BY RANK cannot be combined with an ordinary ORDER BY or with GROUP BY.");

            var translator = implementor.CreateTranslator();
            var names = getRowType().getFieldNames();

            for (var i = 0; i < _projects.size(); i++)
                implementor.Query.SelectProperty((string)names.get(i), translator.Translate((RexNode)_projects.get(i)));

            // Rendered through the one entry point that permits a scoring function, and into the clause
            // rather than the select list — the only place the service accepts one.
            implementor.Query.RankBy = translator.TranslateRank(_rank);

            if (_fetch is not null)
                implementor.Query.Top = RexLiteral.intValue(_fetch);

            // The score is not a column and the projections are computed, so nothing above addresses
            // any of this as a path.
            implementor.Fields = new CosmosPath?[_projects.size()];
        }

    }

}
