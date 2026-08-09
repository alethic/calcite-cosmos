using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// What the adapter reports about the requests it makes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cosmos charges in request units and reports the charge on every response, which is the only real
    /// cost signal available — everything the planner does with cost is inference from declared metadata
    /// and a measured row count. Discarding it means a query that is expensive is expensive silently.
    /// </para>
    /// <para>
    /// <b>Reported through .NET rather than through Calcite</b>, because Calcite has nowhere to put it.
    /// Its <c>Hook</c> values are all plan-time — parse tree, converted, trimmed, program, query plan —
    /// and no adapter in the tree reports execution statistics through it; they use
    /// <c>Hook.QUERY_PLAN</c> and stop, which this does too. A <see cref="Meter"/> and an
    /// <see cref="ActivitySource"/> are what a .NET caller already has a collector for, cost nothing
    /// when nobody is listening, and need no coupling to this assembly.
    /// </para>
    /// <para>
    /// Collect them by name: the meter and the activity source are both
    /// <c>Apache.Calcite.Cosmos.Adapter</c>.
    /// </para>
    /// </remarks>
    public static class CosmosInstrumentation
    {

        /// <summary>
        /// The name both the meter and the activity source are published under.
        /// </summary>
        public const string Name = "Apache.Calcite.Cosmos.Adapter";

        static readonly Meter Meter = new(Name);

        /// <summary>
        /// Traces one execution — a statement or a point read — from first request to last page.
        /// </summary>
        public static readonly ActivitySource ActivitySource = new(Name);

        /// <summary>
        /// Request units charged, one measurement per response.
        /// </summary>
        /// <remarks>
        /// Per response rather than per execution, because a query spanning continuations is charged per
        /// page and the distribution across pages is itself worth seeing. Tagged with the container and
        /// with which kind of request it was, so a point read can be told from the query it replaced.
        /// </remarks>
        public static readonly Histogram<double> RequestCharge =
            Meter.CreateHistogram<double>("cosmos.request_charge", unit: "{request_unit}", description: "Request units charged by a Cosmos response.");

        /// <summary>
        /// Responses received, by container and kind.
        /// </summary>
        /// <remarks>
        /// A query answered in one page and a query answered in forty cost very differently for reasons
        /// the charge alone does not show.
        /// </remarks>
        public static readonly Counter<long> Responses =
            Meter.CreateCounter<long>("cosmos.responses", unit: "{response}", description: "Responses received from Cosmos.");

        /// <summary>
        /// The kind of request a measurement came from.
        /// </summary>
        public static class Kinds
        {

            /// <summary>A statement executed by the query engine.</summary>
            public const string Query = "query";

            /// <summary>A document read directly by id and partition key.</summary>
            public const string PointRead = "point_read";

        }

    }

}
