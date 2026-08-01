using System;
using System.Collections.Generic;
using System.Text;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// A single step in a <see cref="CosmosPath"/>: either a property name or an array index.
    /// </summary>
    public readonly struct CosmosPathSegment : IEquatable<CosmosPathSegment>
    {

        /// <summary>
        /// Creates a segment that accesses a named property.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns>The segment.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        public static CosmosPathSegment Property(string name) => new(name ?? throw new ArgumentNullException(nameof(name)), -1);

        /// <summary>
        /// Creates a segment that accesses an array element.
        /// </summary>
        /// <param name="index">The zero-based index.</param>
        /// <returns>The segment.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
        public static CosmosPathSegment Index(int index) => index >= 0 ? new(null, index) : throw new ArgumentOutOfRangeException(nameof(index));

        readonly string? _name;
        readonly int _index;

        CosmosPathSegment(string? name, int index)
        {
            _name = name;
            _index = index;
        }

        /// <summary>
        /// Gets whether this segment is an array index rather than a property name.
        /// </summary>
        public bool IsIndex => _name is null;

        /// <summary>
        /// Gets the property name, or <c>null</c> when <see cref="IsIndex"/> is <c>true</c>.
        /// </summary>
        public string? Name => _name;

        /// <summary>
        /// Gets the array index, or <c>-1</c> when <see cref="IsIndex"/> is <c>false</c>.
        /// </summary>
        public int ArrayIndex => _index;

        /// <inheritdoc />
        public bool Equals(CosmosPathSegment other) => _index == other._index && string.Equals(_name, other._name, StringComparison.Ordinal);

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is CosmosPathSegment other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(_name, _index);

    }

    /// <summary>
    /// An immutable Cosmos property path rooted at a <c>FROM</c> alias, such as
    /// <c>c.address.city</c> or <c>c["odd name"][0]</c>.
    /// </summary>
    /// <remarks>
    /// Paths are the Cosmos equivalent of a column reference. Because Cosmos documents are
    /// hierarchical, a Calcite field reference generally translates to a path of more than one
    /// segment. Instances are immutable; <see cref="Property"/> and <see cref="Index"/> return
    /// new paths.
    /// </remarks>
    public sealed class CosmosPath : IEquatable<CosmosPath>
    {

        /// <summary>
        /// Creates a path consisting of nothing but the root alias.
        /// </summary>
        /// <param name="alias">The <c>FROM</c> alias the path is rooted at.</param>
        /// <returns>The path.</returns>
        /// <exception cref="ArgumentException"><paramref name="alias"/> is <c>null</c> or empty.</exception>
        public static CosmosPath Root(string alias)
        {
            if (string.IsNullOrEmpty(alias))
                throw new ArgumentException($"'{nameof(alias)}' cannot be null or empty.", nameof(alias));

            return new CosmosPath(alias, Array.Empty<CosmosPathSegment>());
        }

        readonly string _alias;
        readonly CosmosPathSegment[] _segments;

        CosmosPath(string alias, CosmosPathSegment[] segments)
        {
            _alias = alias;
            _segments = segments;
        }

        /// <summary>
        /// Gets the <c>FROM</c> alias this path is rooted at.
        /// </summary>
        public string Alias => _alias;

        /// <summary>
        /// Gets the segments applied to the alias, in order.
        /// </summary>
        public IReadOnlyList<CosmosPathSegment> Segments => _segments;

        /// <summary>
        /// Gets whether this path is the bare alias, with no segments applied.
        /// </summary>
        public bool IsRoot => _segments.Length == 0;

        /// <summary>
        /// Returns a new path with a property access appended.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns>The extended path.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="name"/> is <c>null</c>.</exception>
        public CosmosPath Property(string name) => Append(CosmosPathSegment.Property(name));

        /// <summary>
        /// Returns a new path with an array index access appended.
        /// </summary>
        /// <param name="index">The zero-based index.</param>
        /// <returns>The extended path.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
        public CosmosPath Index(int index) => Append(CosmosPathSegment.Index(index));

        CosmosPath Append(CosmosPathSegment segment)
        {
            var segments = new CosmosPathSegment[_segments.Length + 1];
            Array.Copy(_segments, segments, _segments.Length);
            segments[_segments.Length] = segment;
            return new CosmosPath(_alias, segments);
        }

        /// <summary>
        /// Appends the rendered path to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The buffer to append to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        public void WriteTo(StringBuilder builder)
        {
            if (builder is null)
                throw new ArgumentNullException(nameof(builder));

            builder.Append(_alias);

            foreach (var segment in _segments)
            {
                if (segment.IsIndex)
                    CosmosSql.WriteIndexAccess(builder, segment.ArrayIndex);
                else
                    CosmosSql.WritePropertyAccess(builder, segment.Name!);
            }
        }

        /// <inheritdoc />
        public bool Equals(CosmosPath? other)
        {
            if (other is null)
                return false;
            if (string.Equals(_alias, other._alias, StringComparison.Ordinal) == false)
                return false;
            if (_segments.Length != other._segments.Length)
                return false;

            for (var i = 0; i < _segments.Length; i++)
                if (_segments[i].Equals(other._segments[i]) == false)
                    return false;

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => Equals(obj as CosmosPath);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_alias);
            foreach (var segment in _segments)
                hash.Add(segment);

            return hash.ToHashCode();
        }

        /// <summary>
        /// Returns the rendered Cosmos SQL path.
        /// </summary>
        /// <returns>The path text.</returns>
        public override string ToString()
        {
            var builder = new StringBuilder();
            WriteTo(builder);
            return builder.ToString();
        }

    }

}
