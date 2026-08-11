using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Sql;

using com.google.common.collect;

using java.util;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;
using org.apache.calcite.sql;
using org.apache.calcite.util;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Aggregation implemented in the <see cref="CosmosConvention"/> calling convention, rendered
    /// as a <c>GROUP BY</c> clause with aggregate functions in the projection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cosmos aggregates do not share SQL's null handling, so what may be pushed down is narrow.
    /// Measured against the emulator over the values <c>{10, 20, null, 5}</c> with two documents
    /// lacking the property entirely:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <c>COUNT(c.v)</c> returns 4 — it counts the JSON <c>null</c> and skips only the absent
    /// documents. SQL <c>COUNT(x)</c> excludes nulls and would return 3.
    /// </description></item>
    /// <item><description>
    /// <c>SUM(c.v)</c> and <c>AVG(c.v)</c> return <c>undefined</c> rather than ignoring the null,
    /// where SQL would return 35 and 11.67.
    /// </description></item>
    /// <item><description>
    /// A grouping key that is absent from a document produces a group whose key is omitted from
    /// the result object, rather than a null group.
    /// </description></item>
    /// </list>
    /// <para>
    /// The divergence disappears when the aggregated value cannot be null, which is the same
    /// reasoning applied to sort null placement. So <c>COUNT(*)</c> is always safe, and the value
    /// aggregates are pushed down only over a non-nullable input. <c>COUNT(x)</c> needs no
    /// non-nullable case of its own: Calcite rewrites it to <c>COUNT(*)</c> there before any rule
    /// sees it.
    /// </para>
    /// </remarks>
    public class CosmosAggregate : Aggregate, CosmosRel
    {

        /// <summary>
        /// Determines whether an aggregate call can be rendered faithfully.
        /// </summary>
        /// <param name="call">The aggregate call.</param>
        /// <param name="inputRowType">The row type of the aggregate's input.</param>
        /// <returns><c>true</c> if the call may be pushed down; otherwise <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">Any argument is <c>null</c>.</exception>
        public static bool CanImplement(AggregateCall call, RelDataType inputRowType)
        {
            if (call is null)
                throw new ArgumentNullException(nameof(call));
            if (inputRowType is null)
                throw new ArgumentNullException(nameof(inputRowType));

            // DISTINCT aggregates and FILTER clauses have no Cosmos equivalent.
            if (call.isDistinct() || call.hasFilter() || call.distinctKeys is not null)
                return false;

            // An aggregate carrying its own ordering cannot be expressed either.
            if (call.getCollation() is not null && call.getCollation().getFieldCollations().size() > 0)
                return false;

            var arguments = call.getArgList();

            switch ((SqlKind.__Enum)call.getAggregation().getKind().ordinal())
            {
                case SqlKind.__Enum.COUNT:
                    // COUNT(*) counts rows and agrees with SQL. COUNT(x) does not: Cosmos counts
                    // the JSON null where SQL excludes it. Admitting COUNT(x) over a non-nullable
                    // column — the value aggregates' reasoning — was probed and is dead code:
                    // Calcite rewrites that COUNT(x) to COUNT(*) before any rule sees it.
                    return arguments.size() == 0;

                case SqlKind.__Enum.SUM:
                case SqlKind.__Enum.MIN:
                case SqlKind.__Enum.MAX:
                    return arguments.size() == 1 && IsNonNullable(inputRowType, ((java.lang.Integer)arguments.get(0)).intValue());

                case SqlKind.__Enum.AVG:
                    // Cosmos AVG returns the exact mean; Calcite types an integer AVG as an integer
                    // with truncating division. The fraction cannot be materialized as the declared
                    // type — found by the differential suite, a fractional mean of _ts failing to
                    // read as BIGINT — and truncating it here would still diverge from SQL's
                    // toward-zero division on negatives. So AVG pushes only where the argument is
                    // approximate and the two agree exactly; an integer AVG is carried by
                    // AGGREGATE_REDUCE_FUNCTIONS as SUM and COUNT with the division done above, in
                    // SQL's own semantics.
                    return arguments.size() == 1
                        && IsNonNullable(inputRowType, ((java.lang.Integer)arguments.get(0)).intValue())
                        && IsApproximateNumeric(inputRowType, ((java.lang.Integer)arguments.get(0)).intValue());

                default:
                    return false;
            }
        }

        static bool IsApproximateNumeric(RelDataType rowType, int ordinal)
        {
            var fields = rowType.getFieldList();
            if (ordinal < 0 || ordinal >= fields.size())
                return false;

            return org.apache.calcite.sql.type.SqlTypeName.APPROX_TYPES.contains(((RelDataTypeField)fields.get(ordinal)).getType().getSqlTypeName());
        }

        static bool IsNonNullable(RelDataType rowType, int ordinal)
        {
            var fields = rowType.getFieldList();
            if (ordinal < 0 || ordinal >= fields.size())
                return false;

            return ((RelDataTypeField)fields.get(ordinal)).getType().isNullable() == false;
        }

        /// <summary>
        /// Returns the Cosmos function name for an aggregate call.
        /// </summary>
        static string FunctionName(AggregateCall call)
        {
            return (SqlKind.__Enum)call.getAggregation().getKind().ordinal() switch
            {
                SqlKind.__Enum.COUNT => "COUNT",
                SqlKind.__Enum.SUM => "SUM",
                SqlKind.__Enum.MIN => "MIN",
                SqlKind.__Enum.MAX => "MAX",
                SqlKind.__Enum.AVG => "AVG",
                _ => throw new CosmosTranslationException($"Unsupported aggregate '{call.getAggregation().getName()}'."),
            };
        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the Cosmos convention.</param>
        /// <param name="input">The input node.</param>
        /// <param name="groupSet">The grouping key ordinals.</param>
        /// <param name="groupSets">The grouping sets.</param>
        /// <param name="aggCalls">The aggregate calls.</param>
        public CosmosAggregate(RelOptCluster cluster, RelTraitSet traitSet, RelNode input, ImmutableBitSet groupSet, List? groupSets, List aggCalls) :
            base(cluster, traitSet, ImmutableList.of(), input, groupSet, groupSets, aggCalls)
        {

        }

        /// <inheritdoc />
        public override Aggregate copy(RelTraitSet traitSet, RelNode input, ImmutableBitSet groupSet, List? groupSets, List aggCalls)
        {
            return new CosmosAggregate(getCluster(), traitSet, input, groupSet, groupSets, aggCalls);
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

            // Calcite prunes columns with a projection below the aggregate. Cosmos has one SELECT
            // per statement, so the aggregate's projection replaces it. That is only sound when
            // the pruning projection was path-only — otherwise its computed values are the very
            // things being grouped or aggregated, and they are no longer addressable.
            if (implementor.Query.HasProjection)
            {
                if (implementor.Fields.Count == 0)
                    throw new CosmosTranslationException("An aggregation cannot be applied above a computed projection.");

                implementor.Query.ClearProjection();
            }

            // GROUP BY is applied before OFFSET/LIMIT, so an aggregation above a row restriction
            // would group the whole set rather than the restricted one.
            if (implementor.Query.HasRowLimit)
                throw new CosmosTranslationException("An aggregation cannot be applied above a pushed-down row limit.");

            if (getGroupType() != Group.SIMPLE)
                throw new CosmosTranslationException("Grouping sets have no Cosmos equivalent.");

            // AN AGGREGATE WITH NO CALLS IS A DISTINCT, AND IS EMITTED AS ONE.
            //
            // Grouping by every output column and computing nothing is what SELECT DISTINCT means,
            // and Cosmos has the keyword. Rendering it that way is not a tidier spelling of the
            // same statement: GROUP BY and ORDER BY cannot appear together, and DISTINCT and
            // ORDER BY can — measured — so this is the difference between a sort that pushes and
            // one that does not.
            if (getAggCallList().size() == 0)
            {
                EmitDistinct(implementor);
                return;
            }

            // Cosmos rejects GROUP BY and ORDER BY in the same statement.
            if (implementor.Query.HasOrderBy)
                throw new CosmosTranslationException("Cosmos SQL does not support GROUP BY together with ORDER BY.");

            // Cosmos rejects an aggregate inside an object constructor, so a grouped projection
            // must be written as a flat select list.
            implementor.Query.FlatProjection = true;

            var inputRowType = getInput().getRowType();
            var names = getRowType().getFieldNames();
            var fields = implementor.Fields;
            var output = 0;

            // Calcite orders an aggregate's output as the grouping keys followed by the calls.
            var groupKeys = getGroupSet().asList();
            for (var i = 0; i < groupKeys.size(); i++)
            {
                var index = ((java.lang.Integer)groupKeys.get(i)).intValue();
                if (index < 0 || index >= fields.Count || fields[index] is null)
                    throw new CosmosTranslationException("A grouping key does not resolve to a document path.");

                var path = fields[index]!.ToString();
                implementor.Query.AddGroupBy(path);
                implementor.Query.SelectProperty((string)names.get(output++), path);
            }

            var calls = getAggCallList();
            for (var i = 0; i < calls.size(); i++)
            {
                var call = (AggregateCall)calls.get(i);
                if (CanImplement(call, inputRowType) == false)
                    throw new CosmosTranslationException($"Aggregate '{call}' has no faithful Cosmos equivalent.");

                implementor.Query.SelectProperty((string)names.get(output++), Render(call, fields));
            }

            // Aggregated output is computed, so nothing downstream can address it as a path. Bound
            // per ordinal rather than emptied, so a downstream reference is declined for naming a
            // computed column rather than for being out of range.
            var aggregated = new CosmosPath?[names.size()];
            implementor.Fields = aggregated;
        }

        /// <summary>
        /// Emits a call-less aggregate as <c>SELECT DISTINCT</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The projected paths are the grouping keys, so this is the same statement a
        /// <c>GROUP BY</c> over every key would return — with two differences that matter. It may
        /// carry an <c>ORDER BY</c>, which a grouped statement may not; and its output is still
        /// <em>paths</em>, since nothing is computed, so a sort or a filter above it can address
        /// them. That is why the bindings are kept rather than blanked the way an aggregated row's
        /// are.
        /// </para>
        /// <para>
        /// The flat projection a grouped statement needs is not needed here — nothing is aggregated,
        /// so no aggregate can sit inside an object constructor — and the ordinary object form is
        /// what the row builder above expects.
        /// </para>
        /// </remarks>
        void EmitDistinct(CosmosImplementor implementor)
        {
            var names = getRowType().getFieldNames();
            var fields = implementor.Fields;

            var groupKeys = getGroupSet().asList();
            var projected = new CosmosPath?[names.size()];

            for (var i = 0; i < groupKeys.size(); i++)
            {
                var index = ((java.lang.Integer)groupKeys.get(i)).intValue();
                if (index < 0 || index >= fields.Count || fields[index] is null)
                    throw new CosmosTranslationException("A grouping key does not resolve to a document path.");

                var path = fields[index]!;
                implementor.Query.SelectProperty((string)names.get(i), path.ToString());
                projected[i] = path;
            }

            implementor.Query.Distinct = true;
            implementor.Fields = projected;
        }

        string Render(AggregateCall call, IReadOnlyList<CosmosPath?> fields)
        {
            var arguments = call.getArgList();

            // COUNT(*) is spelled COUNT(1); Cosmos has no star form.
            if (arguments.size() == 0)
                return FunctionName(call) + "(1)";

            var index = ((java.lang.Integer)arguments.get(0)).intValue();
            if (index < 0 || index >= fields.Count || fields[index] is null)
                throw new CosmosTranslationException("An aggregate argument does not resolve to a document path.");

            return $"{FunctionName(call)}({fields[index]})";
        }

    }

}
