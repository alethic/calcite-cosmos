using System;
using System.Linq.Expressions;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Client;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;
using Apache.Calcite.Extensions.Adapter.Enumerable;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.convert;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.runtime;

namespace Apache.Calcite.Cosmos.Adapter.Rel.Convert
{

    /// <summary>
    /// Relational operator that converts a tree of <see cref="CosmosConvention"/> nodes into a
    /// <see cref="ClrAsyncEnumerableConvention"/> result by executing the generated Cosmos SQL against the
    /// container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only way out of the Cosmos convention, and the point at which a pushed-down subtree stops being
    /// a statement and becomes rows. Everything below it contributed to one Cosmos SQL statement; this
    /// renders that statement, executes it, and reads the JSON value each row arrives as into the row the
    /// plan above expects.
    /// </para>
    /// <para>
    /// <b>There is no synchronous counterpart.</b> The v3 Cosmos SDK exposes no synchronous data-plane
    /// API — a page of results arrives only by awaiting <c>FeedIterator.ReadNextAsync</c> — so a converter
    /// into <see cref="ClrEnumerableConvention"/> could only wait on each page and block a thread for the
    /// length of a network round trip. That is the sync-over-async pull
    /// <see cref="ClrAsyncEnumerableConvention"/> refuses converters for, and writing one here would
    /// reintroduce it at the leaf. The consequence is worth stating plainly: a query over a Cosmos table
    /// plans only when the root is asked for in the asynchronous convention.
    /// </para>
    /// <para>
    /// Rendering the statement, resolving the partition key and building the row builder all happen here,
    /// once, while the statement is prepared. Only the sequence and the row builder are on the per-row
    /// path.
    /// </para>
    /// </remarks>
    public class CosmosToClrAsyncEnumerableConverter : ConverterImpl, ClrAsyncEnumerableRel
    {

        static readonly System.Reflection.MethodInfo ReadAsyncMethod = typeof(CosmosSequences).GetMethod(nameof(CosmosSequences.ReadAsync))
            ?? throw new InvalidOperationException($"'{nameof(CosmosSequences.ReadAsync)}' is missing from {nameof(CosmosSequences)}.");

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traits">The trait set, which must carry the asynchronous convention.</param>
        /// <param name="input">The Cosmos subtree being converted.</param>
        public CosmosToClrAsyncEnumerableConverter(RelOptCluster cluster, RelTraitSet traits, RelNode input) :
            base(cluster, ConventionTraitDef.INSTANCE, traits, input)
        {

        }

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosToClrAsyncEnumerableConverter(getCluster(), traitSet, (RelNode)sole(inputs));
        }

        /// <inheritdoc />
        public override RelOptCost computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var cost = base.computeSelfCost(planner, mq);

            return cost == null ? null! : cost.multiplyBy(ClrAsyncEnumerableConvention.CostMultiplier);
        }

        /// <inheritdoc />
        public ClrAsyncEnumerableResult Implement(ClrAsyncEnumerableRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            var input = getInput();

            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());
            var rowType = physType.RowType;

            var (query, fields) = CosmosConverters.GenerateQuery(input, implementor.RexBuilder);

            // A point read returns the document rather than the object the statement constructs, so the
            // two paths need different row builders — and the read needs every output field to address
            // a path, which only this knows. Where one does not, the read is withdrawn and the
            // statement is executed as the query it already is.
            var rowBuilder = query.PointReadId is null
                ? null
                : CosmosConverters.DocumentRowBuilder(physType, getRowType(), fields);

            if (query.PointReadId is not null && rowBuilder is null)
                query = query with { PointReadId = null };

            rowBuilder ??= CosmosConverters.RowBuilder(physType, getRowType());

            Hook.QUERY_PLAN.run(query.Sql);

            return implementor.Result(physType,
                Expression.Call(null,
                    ReadAsyncMethod.MakeGenericMethod(rowType),
                    CosmosConverters.ExecutorExpression(input, implementor.Root),
                    Expression.Constant(query),
                    rowBuilder,
                    // Calcite's cancellation is an AtomicBoolean on the DataContext rather than a token,
                    // and polling one between pages would not interrupt a page in flight. Enumerating the
                    // result is what cancels this, by not asking for the next page.
                    Expression.Constant(CancellationToken.None)));
        }

    }

}
