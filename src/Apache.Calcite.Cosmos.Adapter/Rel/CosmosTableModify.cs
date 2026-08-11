using System;
using System.Linq.Expressions;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Client;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;
using Apache.Calcite.Extensions.Adapter.Enumerable;

using org.apache.calcite.plan;
using org.apache.calcite.prepare;
using org.apache.calcite.rel;
using org.apache.calcite.rel.core;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rex;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Writes the rows of its input to a container, and reports how many it affected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This node is not in <see cref="CosmosConvention"/>, and that is the design rather than an
    /// oversight.</b> A subtree in that convention is a Cosmos SQL statement, and a write is not one:
    /// the query language has no DML, so the write goes through the SDK's item CRUD. Nothing below here
    /// renders text, and no implementor runs. What the node needs is rows, which is what
    /// <c>ClrAsyncEnumerableConvention</c> supplies — the same position <see cref="CosmosLookupJoin"/>
    /// occupies for the same reason.
    /// </para>
    /// <para>
    /// <b>Calcite's own <c>EnumerableTableModify</c> is not the model.</b> It writes through
    /// <c>ModifiableTable.getModifiableCollection()</c>, calling <c>add</c> and <c>remove</c> on a
    /// collection the table hands back. For Cosmos that collection would have to block on
    /// <c>CreateItemAsync</c> per element, which is the sync-over-async pull the asynchronous convention
    /// exists to keep out of a plan. Neither Clr convention has a modify node at all, so this is the
    /// first, and it is free to be shaped by what the service actually offers.
    /// </para>
    /// <para>
    /// The row type is Calcite's DML row type — one <c>BIGINT</c> count — so the node yields exactly one
    /// row, and yields it only once every write has completed.
    /// </para>
    /// </remarks>
    public class CosmosTableModify : TableModify, ClrAsyncEnumerableRel
    {

        static readonly System.Reflection.MethodInfo WriteAsyncMethod = typeof(CosmosSequences).GetMethod(nameof(CosmosSequences.WriteAsync))
            ?? throw new InvalidOperationException($"'{nameof(CosmosSequences.WriteAsync)}' is missing from {nameof(CosmosSequences)}.");

        readonly CosmosTable _table;
        readonly CosmosWriteOperation _write;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the asynchronous convention.</param>
        /// <param name="table">The container being written to.</param>
        /// <param name="catalogReader">The catalog the target was resolved through.</param>
        /// <param name="input">The rows to write, in <see cref="ClrAsyncEnumerableConvention"/>.</param>
        /// <param name="operation">Which statement this stands for.</param>
        /// <param name="updateColumnList">The columns an <c>UPDATE</c> sets, or <c>null</c>.</param>
        /// <param name="sourceExpressionList">The values an <c>UPDATE</c> sets them to, or <c>null</c>.</param>
        /// <param name="flattened">Whether the input's row type has been flattened.</param>
        /// <param name="cosmos">The container's metadata-bearing table.</param>
        /// <param name="write">What the write does to each document.</param>
        /// <param name="partitionKey">The partition a whole-partition delete empties, or <c>null</c> for every other operation.</param>
        public CosmosTableModify(
            RelOptCluster cluster,
            RelTraitSet traitSet,
            RelOptTable table,
            Prepare.CatalogReader catalogReader,
            RelNode input,
            Operation operation,
            java.util.List? updateColumnList,
            java.util.List? sourceExpressionList,
            bool flattened,
            CosmosTable cosmos,
            CosmosWriteOperation write,
            object?[]? partitionKey = null) :
            base(cluster, traitSet, table, catalogReader, input, operation, updateColumnList, sourceExpressionList, flattened)
        {
            _table = cosmos ?? throw new ArgumentNullException(nameof(cosmos));
            _write = write;
            _partitionKey = partitionKey;
        }

        readonly object?[]? _partitionKey;

        /// <summary>
        /// Gets what the write does to each document.
        /// </summary>
        public CosmosWriteOperation Write => _write;

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosTableModify(
                getCluster(),
                traitSet,
                getTable(),
                getCatalogReader(),
                (RelNode)sole(inputs),
                getOperation(),
                getUpdateColumnList(),
                getSourceExpressionList(),
                isFlattened(),
                _table,
                _write,
                _partitionKey);
        }

        /// <inheritdoc />
        /// <remarks>
        /// One request per input row, charged as I/O. There is only ever one plan for a write — nothing
        /// competes with this — so the cost exists to be composed with the input's rather than to decide
        /// anything, and it says the true thing about the input: every row of it becomes a round trip.
        /// </remarks>
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var rows = mq.getRowCount(getInput()).doubleValue();

            return planner.getCostFactory()
                .makeCost(rows, rows, rows)
                .multiplyBy(ClrAsyncEnumerableConvention.CostMultiplier);
        }

        /// <inheritdoc />
        public ClrAsyncEnumerableResult Implement(ClrAsyncEnumerableRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            var input = getInput();
            var inputResult = implementor.VisitChild(this, 0, (ClrAsyncEnumerableRel)input, pref);

            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());

            var inputRowType = input.getRowType();
            var fields = inputRowType.getFieldList();

            // An update's input is the table's row plus one trailing value per SET column; the
            // trailing fields have generated names and are not part of the document, so the write
            // is told the table's columns and the SET names separately.
            string[]? updates = null;
            var documentColumns = fields.size();
            if (getUpdateColumnList() is java.util.List updateColumns && updateColumns.size() > 0)
            {
                updates = new string[updateColumns.size()];
                for (var i = 0; i < updates.Length; i++)
                    updates[i] = (string)updateColumns.get(i);

                documentColumns -= updates.Length;
            }

            var names = new string[documentColumns];
            for (var i = 0; i < names.Length; i++)
                names[i] = ((org.apache.calcite.rel.type.RelDataTypeField)fields.get(i)).getName();

            var paths = new string[_table.Container.PartitionKeyPaths.Count];
            for (var i = 0; i < paths.Length; i++)
                paths[i] = _table.Container.PartitionKeyPaths[i];

            var write = new CosmosWrite(_write, names, paths, updates, _partitionKey);

            return implementor.Result(physType,
                Expression.Call(null,
                    WriteAsyncMethod.MakeGenericMethod(inputResult.PhysType.RowType, physType.RowType),
                    inputResult.Expression,
                    Rel.Convert.CosmosConverters.WriterExpression(getTable(), implementor.Root),
                    Expression.Constant(write),
                    FieldReader(inputResult.PhysType, inputRowType),
                    CountBuilder(physType),
                    Rel.Convert.CosmosConverters.LookupCacheExpression(getTable(), implementor.Root),
                    // Counts the partition a whole-partition delete is about to empty; unused by
                    // every other operation, whose count is the rows it wrote.
                    Rel.Convert.CosmosConverters.PartitionCounterExpression(getTable(), implementor.Root),
                    // As everywhere else on this path: Calcite's cancellation is a flag on the
                    // DataContext rather than a token, and a request in flight would not observe one.
                    Expression.Constant(CancellationToken.None)));
        }

        /// <summary>
        /// Builds the lambda reading a row's values out, boxed and in field order.
        /// </summary>
        /// <remarks>
        /// Boxed because the document builder dispatches on what a value <em>is</em>. It has to: a row
        /// reaching here may hold Java boxes, having been read from a container, or CLR primitives,
        /// having been compiled from a literal.
        /// </remarks>
        static LambdaExpression FieldReader(ClrPhysType physType, org.apache.calcite.rel.type.RelDataType rowType)
        {
            var row = Expression.Parameter(physType.RowType, "row");
            var count = rowType.getFieldCount();

            var values = new Expression[count];
            for (var i = 0; i < count; i++)
            {
                var field = physType.FieldReference(row, i);
                values[i] = field.Type == typeof(object) ? field : Expression.Convert(field, typeof(object));
            }

            return Expression.Lambda(
                typeof(Func<,>).MakeGenericType(physType.RowType, typeof(object[])),
                Expression.NewArrayInit(typeof(object), values),
                row);
        }

        /// <summary>
        /// Builds the lambda wrapping the affected count as this node's own row.
        /// </summary>
        /// <remarks>
        /// The row type is one <c>BIGINT</c>, so by the same arity rule the rest of the adapter follows,
        /// the row is that value rather than an array holding it.
        /// </remarks>
        static LambdaExpression CountBuilder(ClrPhysType physType)
        {
            var count = Expression.Parameter(typeof(long), "count");

            Expression body = count;
            if (body.Type != physType.RowType)
                body = Expression.Convert(body, physType.RowType);

            return Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(long), physType.RowType), body, count);
        }

    }

}
