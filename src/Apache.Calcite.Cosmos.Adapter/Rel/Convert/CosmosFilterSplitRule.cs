using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rex;
using org.apache.calcite.sql;
using org.apache.calcite.sql.fun;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Splits a filter whose condition is partly renderable into the part Cosmos can evaluate and the
    /// part it cannot, so that the service does the work it is able to instead of none of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>WHERE a AND b</c>, where <c>a</c> renders and <c>b</c> does not, becomes <c>WHERE b</c> over
    /// <c>WHERE a</c>. The inner filter converts to a <see cref="CosmosFilter"/> and reaches the service;
    /// the outer stays in Calcite and rechecks what is left. Without this the whole predicate is
    /// declined and every document in the container crosses the wire.
    /// </para>
    /// <para>
    /// <b>Why it is sound.</b> The pushed condition is a conjunction of a subset of the original's
    /// conjuncts, so the original <em>implies</em> it. A weaker filter can only discard rows the original
    /// would have discarded too, and the outer filter — the remaining conjuncts, applied to what
    /// survives — restores exactly the original result. The rule never needs to reason about what a
    /// predicate <em>means</em>, only that dropping conjuncts weakens.
    /// </para>
    /// <para>
    /// <b>Why only conjunctions.</b> Weakening is sound in a positive position and nowhere else.
    /// Dropping a disjunct from <c>a OR b</c> <em>strengthens</em> the predicate and loses every row that
    /// satisfied only the dropped side, so an <c>OR</c> is pushable only when every branch is, which is
    /// what the existing all-or-nothing rule already decides. Under a <c>NOT</c> the polarity flips and
    /// a weaker inner predicate makes a stronger outer one. Top-level conjuncts are the case where
    /// polarity needs no analysis: each one is positive by construction.
    /// </para>
    /// <para>
    /// The split terminates. It fires only when both parts are non-empty, and neither of the two filters
    /// it produces has that property — the inner is wholly renderable, the outer wholly not.
    /// </para>
    /// </remarks>
    public class CosmosFilterSplitRule : RelOptRule
    {

        /// <summary>
        /// Creates a rule instance bound to the specified convention.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits filters over.</param>
        /// <returns>A configured rule.</returns>
        public static CosmosFilterSplitRule Create(CosmosConvention convention)
        {
            return new CosmosFilterSplitRule(convention);
        }

        readonly CosmosConvention _convention;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="convention">The Cosmos convention whose tables this rule splits filters over.</param>
        // RelOptRule's operand builders are deprecated in favour of RelRule.Config, whose operand
        // supplier is a chain of Calcite functional interfaces that costs more ceremony from C# than it
        // buys. The deprecation is "to be removed before 2.0" and the API is unchanged; this is not a
        // rule shape worth expressing twice.
#pragma warning disable CS0612
        public CosmosFilterSplitRule(CosmosConvention convention) :
            base(
                operand((java.lang.Class)typeof(Filter), any()),
                "CosmosFilterSplitRule." + convention.getName())
        {
            _convention = convention;
        }
#pragma warning restore CS0612

        /// <summary>
        /// Returns the container a subtree reads, or <c>null</c> where it does not read one.
        /// </summary>
        /// <remarks>
        /// Walked rather than read off a fixed operand position, because the filter may sit above a
        /// projection or a traversal as easily as above the scan — the split is the same argument
        /// either way, and <see cref="Split"/> already binds through them.
        /// </remarks>
        static CosmosTable? FindTable(RelNode? node)
        {
            if (node is org.apache.calcite.plan.volcano.RelSubset subset)
                node = subset.getOriginal() ?? subset.getBest();

            return node switch
            {
                TableScan scan => scan.getTable()?.unwrap(typeof(CosmosTable)) as CosmosTable,
                Filter filter => FindTable(filter.getInput()),
                Project project => FindTable(project.getInput()),
                Correlate correlate => FindTable(correlate.getLeft()),
                _ => null,
            };
        }

        /// <inheritdoc />
        public override bool matches(RelOptRuleCall call)
        {
            // Scoped to this convention's own container. A rule set is registered per container, and a
            // filter over somebody else's table is not this rule's business.
            if (FindTable(((Filter)call.rel(0)).getInput()) is not CosmosTable table || ReferenceEquals(table.Convention, _convention) == false)
                return false;

            var (pushable, residual) = Split((Filter)call.rel(0));

            return pushable.Count > 0 && residual.Count > 0;
        }

        /// <inheritdoc />
        public override void onMatch(RelOptRuleCall call)
        {
            var filter = (Filter)call.rel(0);

            var (pushable, residual) = Split(filter);
            if (pushable.Count == 0 || residual.Count == 0)
                return;

            var rexBuilder = filter.getCluster().getRexBuilder();

            var inner = filter.copy(filter.getTraitSet(), filter.getInput(), Compose(rexBuilder, pushable));
            var outer = filter.copy(filter.getTraitSet(), inner, Compose(rexBuilder, residual));

            call.transformTo(outer);
        }

        /// <summary>
        /// Partitions a filter's top-level conjuncts by whether each renders as Cosmos SQL.
        /// </summary>
        /// <remarks>
        /// Each conjunct is tested on its own, against the same binding <see cref="CosmosFilterRule"/>
        /// uses, so a conjunct this puts in the pushable half is one that rule would accept whole. An
        /// input whose binding cannot be derived splits into nothing, and the rule does not fire.
        /// </remarks>
        static (List<RexNode> Pushable, List<RexNode> Residual) Split(Filter filter)
        {
            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields) == false)
                return (new List<RexNode>(), new List<RexNode>());

            var rexBuilder = filter.getCluster().getRexBuilder();
            var translator = new CosmosRexTranslator(rexBuilder, fields, new CosmosParameterList());

            var below = AlreadyApplied(filter.getInput());

            var conjuncts = org.apache.calcite.plan.RelOptUtil.conjunctions(filter.getCondition());

            var pushable = new List<RexNode>();
            var residual = new List<RexNode>();

            for (var i = 0; i < conjuncts.size(); i++)
            {
                var conjunct = (RexNode)conjuncts.get(i);

                if (translator.TryTranslate(conjunct, out _))
                {
                    pushable.Add(conjunct);
                    continue;
                }

                // Kept whatever happens: a weakening is a restriction the service can apply, not a
                // replacement for the predicate, so the original is still rechecked above.
                residual.Add(conjunct);

                if (TryWeakenConjunct(conjunct, translator, rexBuilder, CosmosImplementor.DefaultRootAlias) is RexNode weakened &&
                    AlreadyBelow(weakened, below) == false &&
                    translator.TryTranslate(weakened, out _))
                    pushable.Add(weakened);
            }

            return (pushable, residual);
        }

        /// <summary>
        /// Determines whether an expression can tell an absent property from a present one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is what decides whether a branch implies the paths it reads are defined. Measured
        /// against a real account: a comparison on an absent property is <em>undefined</em> rather than
        /// false, and <c>NOT undefined</c> is not true either — so neither <c>c.price &gt; 5</c> nor
        /// <c>NOT (c.price &gt; 5)</c> matches a document without a <c>price</c>, and both therefore
        /// imply the path is defined. Polarity does not come into it, which was the guess and was
        /// wrong.
        /// </para>
        /// <para>
        /// The <c>IS_*</c> family is different: each returns a real boolean for an absent path, so
        /// <c>NOT IS_DEFINED(c.price)</c> is genuinely true there and implies the opposite of what is
        /// wanted. Only <c>IS_DEFINED</c> was measured; the rest are refused with it, because they
        /// answer the same kind of question and a branch using one is rare enough that the caution
        /// costs nothing.
        /// </para>
        /// </remarks>
        static bool ObservesAbsence(RexNode node)
        {
            if (node is not RexCall call)
                return false;

            if (CosmosOperators.IsAbsenceObserving(call.getOperator()))
                return true;

            // SQL's own three-valued tests observe absence too, and they are not in the Cosmos family
            // because they are not Cosmos functions. `x IS NULL` renders as
            // `(NOT IS_DEFINED(x) OR IS_NULL(x))` -- true where the path is missing -- and the IS_TRUE
            // family collapses unknown to a definite boolean, which is the same thing wearing a
            // different hat. Missing these was a live soundness hole: a branch using one can be true
            // with the path absent, so weakening it to IS_DEFINED would have lost exactly those rows.
            switch (call.getKind()?.name())
            {
                case nameof(SqlKind.IS_NULL):
                case nameof(SqlKind.IS_NOT_NULL):
                case nameof(SqlKind.IS_TRUE):
                case nameof(SqlKind.IS_NOT_TRUE):
                case nameof(SqlKind.IS_FALSE):
                case nameof(SqlKind.IS_NOT_FALSE):
                case nameof(SqlKind.IS_UNKNOWN):
                case nameof(SqlKind.IS_DISTINCT_FROM):
                case nameof(SqlKind.IS_NOT_DISTINCT_FROM):
                    return true;
            }

            var operands = call.getOperands();
            for (var i = 0; i < operands.size(); i++)
                if (ObservesAbsence((RexNode)operands.get(i)))
                    return true;

            return false;
        }

        /// <summary>
        /// Collects the document paths an expression reads.
        /// </summary>
        /// <remarks>
        /// The largest sub-expression that resolves wins, so <c>c.a.b</c> is collected rather than
        /// <c>c.a</c> — the deeper path being defined is the stronger statement and is the one implied.
        /// The root is skipped: every document is defined, so <c>IS_DEFINED(c)</c> restricts nothing.
        /// </remarks>
        static void CollectPaths(RexNode node, CosmosRexTranslator translator, string rootAlias, List<RexNode> paths)
        {
            if (translator.TryResolvePath(node, out var path) && path is not null)
            {
                if (path.ToString() != rootAlias)
                    paths.Add(node);

                return;
            }

            if (node is not RexCall call)
                return;

            var operands = call.getOperands();
            for (var i = 0; i < operands.size(); i++)
                CollectPaths((RexNode)operands.get(i), translator, rootAlias, paths);
        }

        /// <summary>
        /// Returns something <paramref name="node"/> implies and Cosmos can evaluate, or <c>null</c>.
        /// </summary>
        static RexNode? TryWeaken(RexNode node, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            if (translator.TryTranslate(node, out _))
                return node;

            if (ObservesAbsence(node))
                return null;

            if (TryBoundNumericCast(node, translator, rexBuilder, rootAlias) is RexNode bounded)
                return bounded;

            var paths = new List<RexNode>();
            CollectPaths(node, translator, rootAlias, paths);

            if (paths.Count == 0)
                return null;

            var terms = new java.util.ArrayList();
            foreach (var path in paths)
                terms.add(rexBuilder.makeCall(CosmosOperators.IsDefined, new[] { path }));

            return RexUtil.composeConjunction(rexBuilder, terms);
        }

        /// <summary>
        /// Bounds the raw value a comparison through a numeric cast reads, or returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>CAST(c."_MAP"['price'] AS INTEGER) &gt; 10</c> has no Cosmos form — Calcite converts the
        /// stored value and the service compares it as it stands, so the two select different documents
        /// — and it is therefore declined and the container read whole. It still <em>implies</em>
        /// something the service can apply, and that is what is pushed:
        /// </para>
        /// <code>
        /// IS_DEFINED(c.price) AND (NOT IS_NUMBER(c.price) OR c.price &gt; 9)
        /// </code>
        /// <para>
        /// <b>Why it is implied.</b> Converting a number to a number moves it by less than one,
        /// whichever way the conversion rounds — so a document whose converted value is greater than 10
        /// has a raw value greater than 9, and one whose converted value equals 30 has a raw value
        /// strictly between 29 and 31. The bound is therefore satisfied by every document the predicate
        /// keeps, which is all a weakening has to be. It is loose on purpose: the exact window depends
        /// on which way the conversion rounds and on the sign, and none of that has to be decided to
        /// make the bound sound.
        /// </para>
        /// <para>
        /// <b>Why the type test is inverted.</b> Calcite converts a stored <em>string</em> too —
        /// measured, <c>= 30</c> matches a document storing <c>"30"</c> — so a filter that kept only
        /// numbers would lose it. Anything that is not a number passes the test untouched and is
        /// rechecked above, where Calcite decides it. In a container whose field really is numeric,
        /// which is the case a typed view describes, that branch matches nothing and the service does
        /// all the work.
        /// </para>
        /// <para>
        /// <c>IS_DEFINED</c> is implied separately: a comparison against an absent property is unknown
        /// in SQL, so no document without one is ever kept.
        /// </para>
        /// </remarks>
        static RexNode? TryBoundNumericCast(RexNode node, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            if (node is not RexCall call || call.getOperands().size() != 2)
                return null;

            var kind = (SqlKind.__Enum)call.getKind().ordinal();
            if (kind is not (SqlKind.__Enum.EQUALS or SqlKind.__Enum.GREATER_THAN or SqlKind.__Enum.GREATER_THAN_OR_EQUAL
                or SqlKind.__Enum.LESS_THAN or SqlKind.__Enum.LESS_THAN_OR_EQUAL))
                return null;

            var left = (RexNode)call.getOperands().get(0);
            var right = (RexNode)call.getOperands().get(1);

            // The bound is on the side the cast is, and a comparison read the other way round is the
            // mirrored operator.
            if (CosmosRexTranslator.TryNumericCastOperand(left) is RexNode value)
            {
            }
            else if (CosmosRexTranslator.TryNumericCastOperand(right) is RexNode mirrored)
            {
                value = mirrored;
                (left, right) = (right, left);
                kind = Mirror(kind);
            }
            else
            {
                return null;
            }

            if (TryConstantNumber(right) is not java.math.BigDecimal bound)
                return null;

            // Rooted at the container rather than at an array-traversal alias, and addressable at all.
            if (translator.TryResolvePath(value, out var path) == false || path is null ||
                string.Equals(path.Alias, rootAlias, StringComparison.Ordinal) == false)
                return null;

            var one = java.math.BigDecimal.ONE;
            var terms = new java.util.ArrayList();

            if (kind is SqlKind.__Enum.EQUALS or SqlKind.__Enum.GREATER_THAN or SqlKind.__Enum.GREATER_THAN_OR_EQUAL)
                terms.add(rexBuilder.makeCall(SqlStdOperatorTable.GREATER_THAN, value, rexBuilder.makeExactLiteral(bound.subtract(one))));

            if (kind is SqlKind.__Enum.EQUALS or SqlKind.__Enum.LESS_THAN or SqlKind.__Enum.LESS_THAN_OR_EQUAL)
                terms.add(rexBuilder.makeCall(SqlStdOperatorTable.LESS_THAN, value, rexBuilder.makeExactLiteral(bound.add(one))));

            if (terms.isEmpty())
                return null;

            var notNumber = rexBuilder.makeCall(SqlStdOperatorTable.NOT, rexBuilder.makeCall(CosmosOperators.IsNumber, new[] { value }));

            var whole = new java.util.ArrayList();
            whole.add(rexBuilder.makeCall(CosmosOperators.IsDefined, new[] { value }));
            whole.add(RexUtil.composeDisjunction(rexBuilder, new java.util.ArrayList { notNumber, RexUtil.composeConjunction(rexBuilder, terms) }));

            return RexUtil.composeConjunction(rexBuilder, whole);
        }

        /// <summary>
        /// Reads the number a constant side of a comparison denotes, through any casts around it.
        /// </summary>
        /// <remarks>
        /// The bound arrives wrapped as often as not — Calcite widens a literal to the type it is being
        /// compared against, so <c>&lt;= 30.5</c> reaches here as <c>CAST(30.5:DECIMAL(3, 1)):DOUBLE</c>
        /// — and a cast of a constant to a number is that constant. Any imprecision in the widening is
        /// far inside the whole unit the bound is loosened by. A literal that is not a number, which is
        /// what a string on this side would be, is refused: nothing is known about where converting it
        /// lands.
        /// </remarks>
        static java.math.BigDecimal? TryConstantNumber(RexNode node)
        {
            while (node is RexCall call &&
                   (SqlKind.__Enum)call.getKind().ordinal() is SqlKind.__Enum.CAST or SqlKind.__Enum.SAFE_CAST &&
                   call.getOperands().size() == 1)
                node = (RexNode)call.getOperands().get(0);

            return node is RexLiteral literal ? literal.getValue() as java.math.BigDecimal : null;
        }

        /// <summary>
        /// The operator a comparison becomes when its operands swap.
        /// </summary>
        static SqlKind.__Enum Mirror(SqlKind.__Enum kind) => kind switch
        {
            SqlKind.__Enum.GREATER_THAN => SqlKind.__Enum.LESS_THAN,
            SqlKind.__Enum.GREATER_THAN_OR_EQUAL => SqlKind.__Enum.LESS_THAN_OR_EQUAL,
            SqlKind.__Enum.LESS_THAN => SqlKind.__Enum.GREATER_THAN,
            SqlKind.__Enum.LESS_THAN_OR_EQUAL => SqlKind.__Enum.GREATER_THAN_OR_EQUAL,
            _ => kind,
        };

        /// <summary>
        /// Returns something a conjunct implies and Cosmos can evaluate, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Only disjunctions were weakened before, which left the commonest untranslatable shape of all
        /// — a single comparison the service has no form for — pushing nothing whatever. A conjunct is
        /// positive by construction, which is the whole of the polarity argument, so the same weakening
        /// applies to it directly.
        /// <para>
        /// A weakening identical to the conjunct is no weakening; it is refused rather than pushed,
        /// which is what the disjunction form has always done for the same reason.
        /// </para>
        /// </remarks>
        static RexNode? TryWeakenConjunct(RexNode conjunct, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            if (TryWeakenDisjunction(conjunct, translator, rexBuilder, rootAlias) is RexNode disjunctive)
                return disjunctive;

            if (TryWeaken(conjunct, translator, rexBuilder, rootAlias) is not RexNode weakened)
                return null;

            return weakened.toString() == conjunct.toString() ? null : weakened;
        }

        /// <summary>
        /// Weakens a disjunction into something the service can apply, or returns <c>null</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Dropping a disjunct strengthens, so an <c>OR</c> is pushable only when every branch is. It
        /// is still pushable when every branch can be <em>weakened</em>: each branch implies its own
        /// weakening, so the disjunction implies the disjunction of them, and a restriction the plan
        /// implies discards nothing the plan would have kept.
        /// </para>
        /// <para>
        /// One branch with no weakening abandons the whole thing, since a disjunction is only as
        /// restrictive as its loosest branch.
        /// </para>
        /// </remarks>
        static RexNode? TryWeakenDisjunction(RexNode conjunct, CosmosRexTranslator translator, RexBuilder rexBuilder, string rootAlias)
        {
            var branches = org.apache.calcite.plan.RelOptUtil.disjunctions(conjunct);
            if (branches.size() < 2)
                return null;

            var weakened = new java.util.ArrayList();

            for (var i = 0; i < branches.size(); i++)
            {
                if (TryWeaken((RexNode)branches.get(i), translator, rexBuilder, rootAlias) is not RexNode branch)
                    return null;

                weakened.add(branch);
            }

            var result = RexUtil.composeDisjunction(rexBuilder, weakened);

            // Nothing gained where every branch already rendered, since the conjunct was pushable then.
            return result.toString() == conjunct.toString() ? null : result;
        }

        /// <summary>
        /// Returns every conjunct any equivalent form of a subtree already applies.
        /// </summary>
        /// <remarks>
        /// A weakening is derived from a conjunct that stays in the residual, and the residual is what
        /// the rule matches on next time round — so without this the same weakening would be pushed
        /// under itself for ever.
        /// <para>
        /// Every equivalent form, not the one the set happens to name: a Volcano input is a set of
        /// equivalent expressions, and which of them <c>getOriginal</c> or <c>getBest</c> returns
        /// depends on how far the planner has got. Reading the whole set is stable, because once a
        /// weakening has been pushed the filter carrying it stays in the set.
        /// </para>
        /// </remarks>
        /// <summary>
        /// Determines whether a weakening is already applied below, conjunct by conjunct.
        /// </summary>
        /// <remarks>
        /// Conjunct by conjunct because that is how a filter stores it. A weakening of more than one
        /// term is pushed as a conjunction and the filter it lands in reports its conjuncts, never the
        /// whole thing — so comparing the composed node against what is below never matched, and the
        /// same weakening was pushed under itself for ever. Measured, as a stack overflow with
        /// TryBindOutput recursing through the filters it had built.
        /// </remarks>
        static bool AlreadyBelow(RexNode weakened, HashSet<string> below)
        {
            var conjuncts = org.apache.calcite.plan.RelOptUtil.conjunctions(weakened);

            for (var i = 0; i < conjuncts.size(); i++)
                if (below.Contains(((RexNode)conjuncts.get(i)).toString()) == false)
                    return false;

            return true;
        }

        static HashSet<string> AlreadyApplied(RelNode? node)
        {
            var applied = new HashSet<string>(StringComparer.Ordinal);

            void Collect(RelNode? candidate)
            {
                if (candidate is not Filter filter)
                    return;

                var conjuncts = org.apache.calcite.plan.RelOptUtil.conjunctions(filter.getCondition());
                for (var i = 0; i < conjuncts.size(); i++)
                    applied.Add(((RexNode)conjuncts.get(i)).toString());
            }

            if (node is org.apache.calcite.plan.volcano.RelSubset subset)
            {
                var alternatives = subset.getRelList();
                for (var i = 0; i < alternatives.size(); i++)
                    Collect((RelNode)alternatives.get(i));

                Collect(subset.getOriginal());
            }
            else
            {
                Collect(node);
            }

            return applied;
        }

        static RexNode Compose(RexBuilder rexBuilder, List<RexNode> conjuncts)
        {
            var list = new java.util.ArrayList();
            foreach (var conjunct in conjuncts)
                list.add(conjunct);

            return RexUtil.composeConjunction(rexBuilder, list);
        }

    }

}
