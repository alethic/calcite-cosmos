using Apache.Calcite.Cosmos.Adapter.Client;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;

using java.util.function;

using org.apache.calcite.plan;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;
using org.apache.calcite.rel.core;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Converts a <see cref="TableModify"/> against a container into a <see cref="CosmosTableModify"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole entry point for writing, and it is this small because Calcite does the hard part.
    /// <c>SqlToRelConverter</c> produces a <c>LogicalTableModify</c> for a table that unwraps to no
    /// <c>ModifiableTable</c> — measured, not assumed — so nothing has to be implemented on the table to
    /// make a write reachable. What the table does supply is column strategies, without which an
    /// <c>INSERT</c> is rejected by the validator before any rule could see it; see
    /// <see cref="CosmosColumnStrategies"/>.
    /// </para>
    /// <para>
    /// <c>INSERT</c> and <c>DELETE</c> only. <c>UPDATE</c> and <c>MERGE</c> are declined rather than
    /// approximated: an update maps onto <c>PatchItemAsync</c>, whose translation from <c>SET</c> clauses
    /// is real work, and a replace standing in for it would silently cost a read-modify-write per row.
    /// A declined modify has no other implementation, so the plan fails rather than doing something
    /// else — which is the correct outcome for a statement the adapter cannot carry out.
    /// </para>
    /// </remarks>
    public class CosmosTableModifyRule : ConverterRule
    {

        /// <summary>
        /// Returns what a modify's operation means to the service, or <c>null</c> where it means nothing
        /// this can do.
        /// </summary>
        static CosmosWriteOperation? GetWrite(TableModify modify)
        {
            var operation = modify.getOperation();

            if (operation == TableModify.Operation.INSERT)
                return CosmosWriteOperation.Insert;

            if (operation == TableModify.Operation.DELETE)
                return CosmosWriteOperation.Delete;

            return null;
        }

        /// <summary>
        /// Determines whether a modify writes to a container in a way this rule can carry out.
        /// </summary>
        static bool IsWritable(TableModify modify)
        {
            return GetWrite(modify) is not null
                && modify.getTable()?.unwrap(typeof(CosmosTable)) is CosmosTable;
        }

        /// <summary>
        /// Creates a rule instance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not bound to a convention, unlike every other rule here, and it cannot be.</b> A
        /// <see cref="ConverterRule"/>'s description is derived from the traits it converts between, and
        /// this one converts <c>NONE</c> to <c>CLR_ASYNC_ENUMERABLE</c> — neither of which names a
        /// container. Two instances built for two containers therefore carry the same description,
        /// rules compare by description, and a planner given both registers one and silently discards
        /// the other. Measured: with a second container's rules registered, an insert into the first
        /// stopped planning, because the surviving instance was checking for the wrong convention.
        /// </para>
        /// <para>
        /// There is nothing for the binding to do anyway. The container is named by the modify, so the
        /// rule reads it from <see cref="TableModify.getTable"/> rather than being told; one instance
        /// serves every container, and registering it once per convention is then merely redundant
        /// rather than wrong.
        /// </para>
        /// </remarks>
        /// <returns>A configured rule.</returns>
        public static CosmosTableModifyRule Create()
        {
            return (CosmosTableModifyRule)Config.INSTANCE
                .withConversion(typeof(TableModify), new DelegatePredicate<TableModify>(IsWritable), Convention.NONE, ClrAsyncEnumerableConvention.Instance, "CosmosTableModifyRule")
                .withRuleFactory(new DelegateFunction<Config, CosmosTableModifyRule>(c => new CosmosTableModifyRule(c)))
                .toRule(typeof(CosmosTableModifyRule));
        }

        /// <summary>
        /// Initializes a new instance using the supplied rule configuration.
        /// </summary>
        /// <param name="config">The rule configuration produced by <see cref="Create"/>.</param>
        public CosmosTableModifyRule(Config config) :
            base(config)
        {

        }

        /// <inheritdoc />
        public override RelNode? convert(RelNode rel)
        {
            var modify = (TableModify)rel;

            if (GetWrite(modify) is not CosmosWriteOperation write)
                return null;

            if (modify.getTable()?.unwrap(typeof(CosmosTable)) is not CosmosTable table)
                return null;

            var input = modify.getInput();

            // simplify(), and it is not optional. A Values node advertises several collations at once —
            // every ordering a single row trivially satisfies — and asking such a trait set for its one
            // collation throws. An INSERT whose source is VALUES is the common case, so without this the
            // rule fails on the first statement anyone writes.
            var inputTraits = input.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance).simplify();

            return new CosmosTableModify(
                modify.getCluster(),
                modify.getTraitSet().replace(ClrAsyncEnumerableConvention.Instance),
                modify.getTable(),
                (Prepare.CatalogReader)modify.getCatalogReader(),
                convert(input, inputTraits),
                modify.getOperation(),
                modify.getUpdateColumnList(),
                modify.getSourceExpressionList(),
                modify.isFlattened(),
                table,
                write);
        }

    }

}
