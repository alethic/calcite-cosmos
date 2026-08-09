using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Threading;

using Apache.Calcite.Cosmos.Adapter.Client;
using Apache.Calcite.Cosmos.Adapter.Rel.Convert;

using Apache.Calcite.Extensions.Adapter.AsyncEnumerable;
using Apache.Calcite.Extensions.Adapter.Enumerable;

using org.apache.calcite.plan;
using org.apache.calcite.rel;
using org.apache.calcite.rel.metadata;
using org.apache.calcite.rel.type;

namespace Apache.Calcite.Cosmos.Adapter.Rel
{

    /// <summary>
    /// Joins a sequence to a container by fetching only the documents its keys could match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A relational join is not expressible in Cosmos — its <c>JOIN</c> has no predicate and traverses a
    /// document's own arrays — so without this both sides are read whole and joined in process. This
    /// makes one side pay for the other: the build side's keys reach the service, which returns the
    /// documents that could join rather than all of them.
    /// </para>
    /// <para>
    /// <b>This is Flink's lookup join, and the shape is worth stating because it is the part that is
    /// easy to get wrong.</b> Calcite expresses "a value that will exist later" as a correlation
    /// variable, and that is a planner-level device: the rule that builds this node consumes it, exactly
    /// as Flink's <c>LogicalCorrelateToJoinFromTemporalTableRule</c> does when its <c>decorrelate()</c>
    /// rewrites every reference as an ordinary field reference. Nothing correlated reaches this node, and
    /// nothing here reads generated code. What crosses into the runtime is key values, which is what
    /// <c>asyncLookup(RowData)</c> receives there.
    /// </para>
    /// <para>
    /// Its left input is in <see cref="ClrAsyncEnumerableConvention"/> and its right in
    /// <see cref="CosmosConvention"/>, which makes it a converter as much as a join — the same shape as
    /// <see cref="Convert.CosmosToClrAsyncEnumerableConverter"/>, and for the same reason: below it is a
    /// statement, above it are rows.
    /// </para>
    /// </remarks>
    public class CosmosLookupJoin : BiRel, ClrAsyncEnumerableRel
    {

        static readonly System.Reflection.MethodInfo JoinAsyncMethod = typeof(CosmosLookup).GetMethod(nameof(CosmosLookup.JoinAsync))
            ?? throw new InvalidOperationException($"'{nameof(CosmosLookup.JoinAsync)}' is missing from {nameof(CosmosLookup)}.");

        /// <summary>
        /// The name prefix of the parameters carrying a batch's keys.
        /// </summary>
        /// <remarks>
        /// Distinct from the <c>@p</c> the statement's own literals use, so the two cannot collide
        /// however many of each a statement has.
        /// </remarks>
        public const string KeyPrefix = "@k";

        /// <summary>
        /// How many build rows one fetch serves, and so how many key parameters the statement carries.
        /// </summary>
        /// <remarks>
        /// Calcite's own batch join defaults to the same number. It is the cardinality bound the whole
        /// idea needs: ten keys in a statement is a large saving, and a million is a statement the
        /// service refuses on length, so the batch is what stops the second from being reachable.
        /// </remarks>
        public const int DefaultBatchSize = 100;

        readonly int _buildKey;
        readonly int _probeKey;
        readonly int _batchSize;
        readonly RelDataType _rowType;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="cluster">The planner cluster.</param>
        /// <param name="traitSet">The trait set, which must carry the asynchronous convention.</param>
        /// <param name="build">The side whose keys are pushed down, in <see cref="ClrAsyncEnumerableConvention"/>.</param>
        /// <param name="probe">The container subtree being restricted, in <see cref="CosmosConvention"/>.</param>
        /// <param name="buildKey">The ordinal of the join key in <paramref name="build"/>'s row.</param>
        /// <param name="probeKey">The ordinal of the join key in <paramref name="probe"/>'s row.</param>
        /// <param name="rowType">The joined row type: the build's fields followed by the probe's.</param>
        /// <param name="batchSize">How many build rows one fetch serves.</param>
        public CosmosLookupJoin(RelOptCluster cluster, RelTraitSet traitSet, RelNode build, RelNode probe, int buildKey, int probeKey, RelDataType rowType, int batchSize = DefaultBatchSize) :
            base(cluster, traitSet, build, probe)
        {
            _buildKey = buildKey;
            _probeKey = probeKey;
            _rowType = rowType ?? throw new ArgumentNullException(nameof(rowType));
            _batchSize = batchSize > 0 ? batchSize : throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        /// <summary>Gets the ordinal of the join key in the build side's row.</summary>
        public int BuildKey => _buildKey;

        /// <summary>Gets the ordinal of the join key in the container subtree's row.</summary>
        public int ProbeKey => _probeKey;

        /// <summary>Gets how many build rows one fetch serves.</summary>
        public int BatchSize => _batchSize;

        /// <inheritdoc />
        protected override RelDataType deriveRowType()
        {
            return _rowType;
        }

        /// <inheritdoc />
        public override RelNode copy(RelTraitSet traitSet, java.util.List inputs)
        {
            return new CosmosLookupJoin(getCluster(), traitSet, (RelNode)inputs.get(0), (RelNode)inputs.get(1), _buildKey, _probeKey, _rowType, _batchSize);
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// Charged for what it actually does: read the build side, and make one request per batch of it.
        /// The alternative the planner is comparing against reads the whole container once, so the
        /// comparison turns on the container being larger than the build side — which is the case this
        /// exists for, and the case where the row count read from the service earns its keep.
        /// </para>
        /// <para>
        /// The rows fetched are charged rather than the container's, since that is the point: only the
        /// documents a key could match are read.
        /// </para>
        /// </remarks>
        public override RelOptCost? computeSelfCost(RelOptPlanner planner, RelMetadataQuery mq)
        {
            var buildRows = mq.getRowCount(getLeft()).doubleValue();
            var rows = mq.getRowCount(this).doubleValue();

            // One request per batch, and each is a round trip rather than a row read — which is why the
            // batch size divides here and appears nowhere else in the cost.
            var requests = System.Math.Ceiling(buildRows / _batchSize);

            return planner.getCostFactory()
                .makeCost(rows, buildRows + requests, rows)
                .multiplyBy(ClrAsyncEnumerableConvention.CostMultiplier);
        }

        /// <inheritdoc />
        public ClrAsyncEnumerableResult Implement(ClrAsyncEnumerableRelImplementor implementor, ClrEnumerablePrefer pref)
        {
            var build = getLeft();
            var probe = getRight();

            var buildResult = implementor.VisitChild(this, 0, (ClrAsyncEnumerableRel)build, pref);

            var probePhysType = ClrPhysTypeImpl.Of(implementor.TypeFactory, probe.getRowType(), pref.PreferArray());
            var physType = ClrPhysTypeImpl.Of(implementor.TypeFactory, getRowType(), pref.PreferArray());

            var (query, _) = CosmosConverters.GenerateLookupQuery(probe, implementor.RexBuilder, _probeKey, KeyPrefix, _batchSize);

            org.apache.calcite.runtime.Hook.QUERY_PLAN.run(query.Sql);

            var buildType = buildResult.PhysType.RowType;
            var probeType = probePhysType.RowType;
            var rowType = physType.RowType;

            return implementor.Result(physType,
                Expression.Call(null,
                    JoinAsyncMethod.MakeGenericMethod(buildType, probeType, rowType),
                    buildResult.Expression,
                    CosmosConverters.ExecutorExpression(probe, implementor.Root),
                    Expression.Constant(query),
                    Expression.Constant(KeyPrefix),
                    Expression.Constant(_batchSize),
                    KeySelector(buildResult.PhysType, buildType, _buildKey),
                    CosmosConverters.RowBuilder(probePhysType, probe.getRowType()),
                    KeySelector(probePhysType, probeType, _probeKey),
                    ResultSelector(physType, buildResult.PhysType, buildType, probePhysType, probeType),
                    // As for the converter: Calcite's cancellation is a flag on the DataContext rather
                    // than a token, and a batch in flight would not observe one. Not asking for the next
                    // batch is what stops this.
                    Expression.Constant(CancellationToken.None)));
        }

        /// <summary>
        /// Builds the lambda reading the join key out of a row.
        /// </summary>
        /// <remarks>
        /// Boxed, because the two sides' keys are compared as objects — see
        /// <see cref="CosmosLookup.Normalize"/> for why they cannot simply be compared as they arrive.
        /// </remarks>
        static LambdaExpression KeySelector(ClrPhysType physType, Type rowType, int ordinal)
        {
            var row = Expression.Parameter(rowType, "row");

            return Expression.Lambda(Expression.Convert(physType.FieldReference(row, ordinal), typeof(object)), row);
        }

        /// <summary>
        /// Builds the lambda combining a matching pair into a joined row.
        /// </summary>
        /// <remarks>
        /// Written out rather than borrowed from <c>ClrEnumUtils.JoinSelector</c>, which takes the
        /// implementor by an interface internal to <c>Apache.Calcite.Extensions</c> and so cannot be
        /// called from here. What it does is this: the build's fields in order, then the probe's.
        /// </remarks>
        LambdaExpression ResultSelector(ClrPhysType physType, ClrPhysType buildPhysType, Type buildType, ClrPhysType probePhysType, Type probeType)
        {
            var b = Expression.Parameter(buildType, "b");
            var p = Expression.Parameter(probeType, "p");

            var fields = new List<Expression>();

            for (var i = 0; i < getLeft().getRowType().getFieldCount(); i++)
                fields.Add(buildPhysType.FieldReference(b, i));

            for (var i = 0; i < getRight().getRowType().getFieldCount(); i++)
                fields.Add(probePhysType.FieldReference(p, i));

            return Expression.Lambda(physType.Record(fields), b, p);
        }

    }

}
