using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A value bound to a Cosmos SQL query parameter.
    /// </summary>
    /// <param name="Name">The parameter name, including the leading <c>@</c>.</param>
    /// <param name="Value">The bound value.</param>
    public readonly record struct CosmosParameter(string Name, object? Value);

    /// <summary>
    /// Accumulates the parameters bound by a query under construction, handing out names as
    /// values are added.
    /// </summary>
    /// <remarks>
    /// Values that could originate from user input are bound rather than inlined, so that the
    /// generated statement text never varies with the data. Planner-generated constants such as
    /// fetch counts are inlined instead — see <see cref="CosmosSql.WriteLiteral"/>.
    /// </remarks>
    public sealed class CosmosParameterList
    {

        readonly List<CosmosParameter> _parameters = new();
        readonly string _prefix;

        /// <summary>
        /// Initializes a new instance using the default <c>@p</c> name prefix.
        /// </summary>
        public CosmosParameterList() :
            this("@p")
        {

        }

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="prefix">The name prefix, including the leading <c>@</c>. Parameters are named by appending an ordinal.</param>
        /// <exception cref="ArgumentException"><paramref name="prefix"/> is <c>null</c>, empty, or does not begin with <c>@</c>.</exception>
        public CosmosParameterList(string prefix)
        {
            if (string.IsNullOrEmpty(prefix))
                throw new ArgumentException($"'{nameof(prefix)}' cannot be null or empty.", nameof(prefix));
            if (prefix[0] != '@')
                throw new ArgumentException($"'{nameof(prefix)}' must begin with '@'.", nameof(prefix));

            _prefix = prefix;
        }

        /// <summary>
        /// Gets the parameters bound so far, in the order they were added.
        /// </summary>
        public IReadOnlyList<CosmosParameter> Parameters => _parameters;

        /// <summary>
        /// Gets the number of parameters bound so far.
        /// </summary>
        public int Count => _parameters.Count;

        /// <summary>
        /// Binds <paramref name="value"/> and returns the name to emit in its place.
        /// </summary>
        /// <param name="value">The value to bind.</param>
        /// <returns>The parameter name, including the leading <c>@</c>.</returns>
        public string Add(object? value)
        {
            var name = _prefix + _parameters.Count.ToString(CultureInfo.InvariantCulture);
            _parameters.Add(new CosmosParameter(name, value));
            return name;
        }

    }

}
