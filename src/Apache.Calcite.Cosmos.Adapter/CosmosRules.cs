using System;
using System.Collections.Generic;

using Apache.Calcite.Cosmos.Adapter.Rel.Convert;

using org.apache.calcite.plan;

namespace Apache.Calcite.Cosmos.Adapter
{

    /// <summary>
    /// The conversion rules registered for a <see cref="CosmosConvention"/> instance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rules are per-convention rather than static, because a convention is bound to a container
    /// and some rules must consult that container's metadata to decide whether they may fire.
    /// </para>
    /// <para>
    /// Several operators are deliberately absent and must not be added without revisiting the
    /// design:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>No join rule.</b> Cosmos <c>JOIN</c> has no predicate — it cross-products a document
    /// with its own nested arrays. Relational joins are inexpressible. Array traversal arrives via
    /// <c>Uncollect</c>/<c>Correlate</c> instead.
    /// </description></item>
    /// <item><description>
    /// <b>No set operation rules.</b> Cosmos has no <c>UNION</c>, <c>INTERSECT</c>, or
    /// <c>EXCEPT</c>. These are evaluated by Calcite in-process.
    /// </description></item>
    /// <item><description>
    /// <b>No values rule.</b> There is no container-independent row source.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static class CosmosRules
    {

        /// <summary>
        /// Returns the rules to register for the given convention.
        /// </summary>
        /// <param name="convention">The convention the rules are bound to.</param>
        /// <returns>The rules.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="convention"/> is <c>null</c>.</exception>
        public static IEnumerable<RelOptRule> GetRules(CosmosConvention convention)
        {
            if (convention is null)
                throw new ArgumentNullException(nameof(convention));

            yield return CosmosAggregateRule.Create(convention);
            yield return CosmosFilterRule.Create(convention);
            yield return CosmosProjectRule.Create(convention);
            yield return CosmosSortRule.Create(convention);
            yield return CosmosUnnestRule.Create(convention);
        }

    }

}
