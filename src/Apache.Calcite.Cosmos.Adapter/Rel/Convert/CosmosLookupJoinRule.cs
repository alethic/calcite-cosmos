using Apache.Calcite.Cosmos.Adapter.Sql;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;
using org.apache.calcite.rel.core;
using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="Join"/> against a container into a <see cref="CosmosLookupJoin"/>, so that
    /// only the documents the other side's keys could match are fetched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the rule half of Flink's lookup join. Its counterpart there,
    /// <c>LogicalCorrelateToJoinFromTemporalTableRule</c>, is where the correlation variable goes: the
    /// rule consumes it and the physical node never sees one. Here there is not even that much to do,
    /// because the shape arrives as an ordinary equi-join and the keys are already ordinals.
    /// </para>
    /// <para>
    /// Declines far more than it accepts, and each refusal is a case where fetching per batch would
    /// answer a different question than the plan asked:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Inner joins only.</b> An outer join has to preserve rows with no match, and a semi or anti
    /// join is a different operator; each is expressible, none is written yet, and a wrong one is a
    /// wrong answer. They fall through to being joined in process, which is what happens today.
    /// </description></item>
    /// <item><description>
    /// <b>One equality, and nothing else in the condition.</b> A residual predicate would have to be
    /// applied after the fetch, and is not.
    /// </description></item>
    /// <item><description>
    /// <b>Nothing above the container's scan that a restriction would change the meaning of.</b> A
    /// <c>LIMIT</c> is the clear case: <c>TOP 5</c> of the container is not <c>TOP 5</c> of each batch,
    /// so running it per batch returns rows the plan did not ask for. Grouping is the same argument —
    /// a count per batch is not a count.
    /// </description></item>
    /// <item><description>
    /// <b>A key that resolves to a document path and can be a parameter.</b> A computed projection has
    /// no path for a <c>WHERE</c> to name, and a type Cosmos has no counterpart for cannot be bound.
    /// </description></item>
    /// </list>
    /// </remarks>
    public class CosmosLookupJoinRule : ConverterRule
    {

        /// <summary>
        /// Determines whether a value of the given type can be carried as a key parameter.
        /// </summary>
        /// <remarks>
        /// The types Cosmos itself has: a string, a number, or a boolean. A parameter is serialised into
        /// JSON, and a value with no JSON counterpart would either fail there or match nothing.
        /// <para>
        /// <c>ANY</c> is refused, which is the type of every promoted partition key column and so costs
        /// this on a join between two containers. Keys are also compared in process once fetched, and
        /// <c>ANY</c> says nothing about what is being compared.
        /// </para>
        /// </remarks>
        static bool IsBindableKey(org.apache.calcite.rel.type.RelDataType type)
        {
            return type?.getSqlTypeName()?.getName() switch
            {
                nameof(SqlTypeName.CHAR) or nameof(SqlTypeName.VARCHAR) => true,
                nameof(SqlTypeName.BOOLEAN) => true,
                nameof(SqlTypeName.TINYINT) or nameof(SqlTypeName.SMALLINT) or nameof(SqlTypeName.INTEGER) or nameof(SqlTypeName.BIGINT) => true,
                nameof(SqlTypeName.FLOAT) or nameof(SqlTypeName.REAL) or nameof(SqlTypeName.DOUBLE) or nameof(SqlTypeName.DECIMAL) => true,
                _ => false,
            };
        }

        /// <summary>
        /// Determines whether restricting a subtree to a batch's keys leaves it meaning what it meant.
        /// </summary>
        /// <remarks>
        /// A filter, a projection or a traversal restricts row by row, so an added conjunct composes
        /// with it. A sort, a limit or a grouping does not: each is defined over the whole result, and
        /// per batch it would be defined over a fraction of it.
        /// </remarks>
        static bool IsRestrictable(RelNode? node)
        {
            if (node is org.apache.calcite.plan.volcano.RelSubset subset)
                node = subset.getOriginal() ?? subset.getBest();

            return node switch
            {
                null => false,
                TableScan scan => scan.getTable()?.unwrap(typeof(CosmosTable)) is CosmosTable,
                Filter filter => IsRestrictable(filter.getInput()),
                Project project => IsRestrictable(project.getInput()),
                Correlate correlate => IsRestrictable(correlate.getLeft()),
                _ => false,
            };
        }

        /// <summary>
        /// Returns the container a subtree reads, or <c>null</c> where it does not read one.
        /// </summary>
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

        /// <summary>
        /// Returns the single pair of key ordinals a join matches on, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// The probe ordinal is returned relative to the probe's own row rather than to the joined row,
        /// which is what <see cref="JoinInfo"/> already gives.
        /// </remarks>
        static (int Build, int Probe)? GetKeys(Join join)
        {
            var info = join.analyzeCondition();

            if (info.isEqui() == false || info.leftKeys.size() != 1 || info.rightKeys.size() != 1)
                return null;

            return (((java.lang.Integer)info.leftKeys.get(0)).intValue(), ((java.lang.Integer)info.rightKeys.get(0)).intValue());
        }

        /// <summary>
        /// Creates a rule instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not bound to a convention</b>, for the reason
        /// <see cref="CosmosTableModifyRule.Create"/> is not: a <see cref="ConverterRule"/>'s
        /// description is derived from the traits it converts between, and this one converts
        /// <c>NONE</c> to <c>CLR_ASYNC_ENUMERABLE</c> — neither of which names a container. One
        /// instance per container therefore gives several rules carrying the same description, and
        /// which of them a planner matches with is then not something the adapter decides.
        /// </para>
        /// <para>
        /// It decided wrongly, measured on a join across three containers: one of the two joins fetched
        /// by key and the other read its container whole. Two containers never showed it, for a reason
        /// that is worth knowing and not worth relying on —
        /// <c>CosmosLookupJoinPlannerTests.EveryContainerInAQueryCanBeOnTheProbeSide</c> records both.
        /// </para>
        /// <para>
        /// The binding had nothing to do anyway. The container is named by the probe side, which
        /// <see cref="FindTable"/> must locate regardless in order to establish that the subtree is a
        /// container's at all, so the rule reads the convention from there rather than being told it.
        /// One instance serves every container, and registering it once per convention is then merely
        /// redundant rather than wrong.
        /// </para>
        /// </remarks>
        /// <returns>A configured rule.</returns>
        public static CosmosLookupJoinRule Create()
        {
            static bool IsTranslatable(Join join)
            {
                if (join.getJoinType() != JoinRelType.INNER)
                    return false;

                if (GetKeys(join) is not (int build, int probe))
                    return false;

                var right = join.getRight();

                // The probe side must bottom out at a container's scan — both because there is
                // otherwise nothing to fetch from, and because the convention this converts that side
                // into is the container's own.
                if (FindTable(right) is null)
                    return false;

                if (IsRestrictable(right) == false)
                    return false;

                if (CosmosImplementor.TryBindOutput(right, out var fields) == false)
                    return false;

                if (probe < 0 || probe >= fields.Count || fields[probe] is null)
                    return false;

                var field = (org.apache.calcite.rel.type.RelDataTypeField)right.getRowType().getFieldList().get(probe);
                if (IsBindableKey(field.getType()) == false)
                    return false;

                var buildField = (org.apache.calcite.rel.type.RelDataTypeField)join.getLeft().getRowType().getFieldList().get(build);

                return IsBindableKey(buildField.getType());
            }

            return (CosmosLookupJoinRule)Config.INSTANCE
                .withConversion(typeof(Join), new DelegatePredicate<Join>(IsTranslatable), Convention.NONE, ClrAsyncEnumerableConvention.Instance, "CosmosLookupJoinRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosLookupJoinRule>(c => new CosmosLookupJoinRule(c)))
                .toRule(typeof(CosmosLookupJoinRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosLookupJoinRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode? convert(RelNode rel)
        {
            var join = (Join)rel;

            if (GetKeys(join) is not (int build, int probe))
                return null;

            var left = join.getLeft();
            var right = join.getRight();

            if (FindTable(right) is not CosmosTable table)
                return null;

            return new CosmosLookupJoin(
                join.getCluster(),
                join.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance),
                convert(left, left.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance)),
                convert(right, right.getTraitSet().replace(table.Convention)),
                join.getCondition(),
                build,
                probe);
        }

    }

}
