using System;

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
    /// <c>INSERT</c>, <c>DELETE</c>, and <c>UPDATE</c> — the last carried as a whole-document
    /// replace, which is what SQL's whole-value assignment says, priced as what it is. What is
    /// declined is an <c>UPDATE</c> whose <c>SET</c> names the <c>id</c> or a partition key column:
    /// identity and placement are not the statement's to change — the service forbids both — and a
    /// declined modify has no other implementation, so the plan fails rather than doing something
    /// else. <c>MERGE</c> is declined whole. The cheaper carriage of a targeted <c>SET</c> as a
    /// patch is recorded in <c>DESIGN.md</c> under <em>Updating</em> and not yet taken.
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
                return TryWholePartition(modify, out _) ? CosmosWriteOperation.DeletePartition : CosmosWriteOperation.Delete;

            if (operation == TableModify.Operation.UPDATE)
                return CosmosWriteOperation.Update;

            return null;
        }

        /// <summary>
        /// Determines whether a delete empties exactly one logical partition, and the account will
        /// do that in one request.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two questions, and the order matters: the predicate's shape is free to ask, the
        /// account's capability costs a round trip. So the shape is recognised first and the
        /// capability asked only for a statement that could use it — a plan with no
        /// whole-partition delete in it never probes at all.
        /// </para>
        /// <para>
        /// Where either answer is no the delete stays what it was: a scan, and a delete per
        /// document. Nothing here can turn a working plan into a refused request.
        /// </para>
        /// </remarks>
        static bool TryWholePartition(TableModify modify, out System.Collections.Generic.IReadOnlyList<object?> values)
        {
            values = System.Array.Empty<object?>();

            if (modify.getTable()?.unwrap(typeof(CosmosTable)) is not CosmosTable table)
                return false;

            if (FindFilter(modify.getInput()) is not Filter filter)
                return false;

            if (CosmosImplementor.TryBindOutput(filter.getInput(), out var fields) == false)
                return false;

            if (Metadata.CosmosPartitionKeyExtractor.TryExtractWholePartition(filter.getCondition(), fields, table.Container, CosmosImplementor.DefaultRootAlias, out var pinned) == false)
                return false;

            if (table.Container.SupportsPartitionKeyDelete == false)
                return false;

            values = pinned;
            return true;
        }

        /// <summary>
        /// Returns the filter a modify's input reads through, or <c>null</c> where it has none.
        /// </summary>
        static Filter? FindFilter(RelNode? node)
        {
            if (node is org.apache.calcite.plan.volcano.RelSubset subset)
                node = subset.getOriginal() ?? subset.getBest();

            return node switch
            {
                Filter filter => filter,
                Project project => FindFilter(project.getInput()),
                _ => null,
            };
        }

        /// <summary>
        /// Determines whether a modify writes to a container in a way this rule can carry out.
        /// </summary>
        static bool IsWritable(TableModify modify)
        {
            if (modify.getTable()?.unwrap(typeof(CosmosTable)) is not CosmosTable table)
                return false;

            if (GetWrite(modify) is not CosmosWriteOperation write)
                return false;

            return write != CosmosWriteOperation.Update || SetsOnlyMutableColumns(modify, table);
        }

        /// <summary>
        /// Determines whether every column an <c>UPDATE</c> sets is one a replace may change.
        /// </summary>
        /// <remarks>
        /// <c>id</c> is identity and a partition key is placement; the service forbids changing
        /// either on an existing document, so a <c>SET</c> naming one is refused here, where the
        /// refusal is a plan that fails, rather than at the service, where it would be a request
        /// that fails per row. The map column may still <em>carry</em> a different identity or
        /// placement inside its value — that cannot be seen at plan time, and the service rejects
        /// the resulting request loudly.
        /// </remarks>
        static bool SetsOnlyMutableColumns(TableModify modify, CosmosTable table)
        {
            var columns = modify.getUpdateColumnList();
            if (columns is null || columns.size() == 0)
                return false;

            for (var i = 0; i < columns.size(); i++)
            {
                if (columns.get(i)?.ToString() is not string name)
                    return false;

                if (string.Equals(name, Metadata.CosmosContainerMetadata.IdPropertyName, StringComparison.Ordinal))
                    return false;

                foreach (var path in table.Container.PartitionKeyPaths)
                    if (string.Equals(path.TrimStart('/'), name, StringComparison.Ordinal))
                        return false;
            }

            return true;
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

            // Recovered again rather than carried from the predicate check: a rule's match and its
            // conversion are separate calls, and the second is where the value has to be right.
            object?[]? partitionKey = null;
            if (write == CosmosWriteOperation.DeletePartition && TryWholePartition(modify, out var pinned))
                partitionKey = System.Linq.Enumerable.ToArray(pinned);

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
                write,
                partitionKey);
        }

    }

}
