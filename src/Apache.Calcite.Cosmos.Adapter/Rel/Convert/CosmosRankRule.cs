using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Recognises an ordering by a scoring function and turns it into a <see cref="CosmosRank"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ORDER BY FULLTEXTSCORE(…)</c> orders by an expression that is not in the select list, and
    /// Calcite expresses that as three nodes: a projection that <em>adds</em> the expression as a column,
    /// a sort on that column, and a projection that drops it again.
    /// </para>
    /// <code>
    /// LogicalProject(id=[$0])                                    drops the score
    ///   LogicalSort(sort0=[$1])                                  sorts on it
    ///     LogicalProject(id=[$1], $f1=[FULLTEXTSCORE($0, 'kw')])  adds it
    ///       TableScan
    /// </code>
    /// <para>
    /// The middle step is the problem: the reference is explicit that a scoring function <b>cannot be
    /// part of a projection</b>, so the first of those three nodes is a statement Cosmos will not run.
    /// The whole shape therefore has to collapse into one node that projects what survives and ranks by
    /// the score without ever selecting it — which is what <see cref="CosmosRank"/> is.
    /// </para>
    /// <para>
    /// Matching all three at once is also what makes it safe. Seeing only the sort would leave the score
    /// column in the row type for something above to read, and it would read null, since the statement
    /// never projects it. Requiring the outer projection to discard it is how this knows nothing does.
    /// </para>
    /// </remarks>
    public class CosmosRankRule : RelOptRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosRankRule Create(CosmosConvention convention)
        {
            return new CosmosRankRule(convention);
        }

        readonly CosmosConvention _convention;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention this rule targets.</param>
#pragma warning disable CS0612
        public CosmosRankRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(Project),
                    operand((java.lang.Class)typeof(Sort),
                        operand((java.lang.Class)typeof(Project), any()))),
                "CosmosRankRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            return Analyse(call) is not null;
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            if (Analyse(call) is not Match match)
                return;

            var inner = (Project)call.rel(2);

            call.transformTo(new CosmosRank(
                inner.getCluster(),
                inner.getTraitSet().replace(_convention),
                org.apache.calcite.plan.RelOptRule.convert(inner.getInput(), inner.getInput().getTraitSet().replace(_convention)),
                match.Projects,
                ((Project)call.rel(0)).getRowType(),
                match.Rank,
                match.Fetch));
        }

        /// <summary>
        /// What the matched shape amounts to, or <c>null</c> where it is not one this can render.
        /// </summary>
        sealed record Match(java.util.List Projects, RexNode Rank, RexNode? Fetch);

        /// <summary>
        /// Reads the three nodes and decides whether they are an ordering by a scoring function that
        /// this can express, resolving everything the resulting node will need.
        /// </summary>
        Match? Analyse(RelOptRuleCall call)
        {
            var outer = (Project)call.rel(0);
            var sort = (Sort)call.rel(1);
            var inner = (Project)call.rel(2);

            // One key, and it must be the score. Anything else is an ordinary sort, and a scoring
            // function combined with ordering on a property path is refused by the service anyway.
            var collations = sort.getCollation().getFieldCollations();
            if (collations.size() != 1)
                return null;

            var key = ((org.apache.calcite.rel.RelFieldCollation)collations.get(0)).getFieldIndex();
            if (key < 0 || key >= inner.getProjects().size())
                return null;

            var rank = (RexNode)inner.getProjects().get(key);
            if (CosmosRexTranslator.IsScoringFunction(rank) == false)
                return null;

            // Offset is refused rather than guessed at: the documented pairings for the clause are TOP
            // and OFFSET LIMIT, and only TOP is expressible here without also ordering.
            if (sort.offset is not null)
                return null;

            // Every surviving column must come straight through, and none of them may be the score.
            // A computed outer projection would have to be rebuilt over the inner one, and a reference
            // to the score would read a column the statement never projects.
            var projects = new java.util.ArrayList();

            for (var i = 0; i < outer.getProjects().size(); i++)
            {
                if ((RexNode)outer.getProjects().get(i) is not RexInputRef reference)
                    return null;

                var ordinal = reference.getIndex();
                if (ordinal == key || ordinal < 0 || ordinal >= inner.getProjects().size())
                    return null;

                projects.add(inner.getProjects().get(ordinal));
            }

            if (projects.isEmpty())
                return null;

            // Everything has to render, and against the binding implementation will use.
            if (CosmosImplementor.TryBindOutput(inner.getInput(), out var fields) == false)
                return null;

            var translator = new CosmosRexTranslator(inner.getCluster().getRexBuilder(), fields, new CosmosParameterList());

            for (var i = 0; i < projects.size(); i++)
                if (translator.TryTranslate((RexNode)projects.get(i), out _) == false)
                    return null;

            try
            {
                translator.TranslateRank(rank);
            }
            catch (CosmosTranslationException)
            {
                return null;
            }

            return new Match(projects, rank, sort.fetch);
        }

    }

}
