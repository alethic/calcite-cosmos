using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Apache.Calcite.Cosmos.Adapter.Sql
{

    /// <summary>
    /// Lexical primitives for emitting Cosmos SQL: identifier classification, property access,
    /// and JSON literal rendering.
    /// </summary>
    /// <remarks>
    /// Cosmos SQL is case-sensitive and its literals are JSON values. This type is deliberately
    /// low-level; it knows nothing about relational algebra and performs no validation beyond
    /// what is required to emit syntactically correct text.
    /// </remarks>
    public static class CosmosSql
    {

        /// <summary>
        /// Words that cannot appear as a bare identifier. Property access falls back to bracket
        /// notation for any of these.
        /// </summary>
        /// <remarks>
        /// Covers the documented reserved keywords plus the clause keywords. Bracket notation is
        /// always legal, so over-inclusion here is harmless while under-inclusion is not.
        /// </remarks>
        static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "AND", "ARRAY", "AS", "ASC", "BETWEEN", "BY", "DESC", "DISTINCT", "EXISTS", "FROM",
            "GROUP", "IN", "JOIN", "LIMIT", "LIKE", "NOT", "NULL", "OFFSET", "OR", "ORDER",
            "ROOT", "SELECT", "TOP", "UDF", "VALUE", "WHERE",
        };

        /// <summary>
        /// Determines whether <paramref name="name"/> can be written as a bare identifier after a
        /// dot, rather than requiring bracket notation.
        /// </summary>
        /// <param name="name">The property name to classify.</param>
        /// <returns><c>true</c> if the name may follow a dot unquoted; otherwise <c>false</c>.</returns>
        public static bool IsBareIdentifier(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;

            if (name[0] is >= '0' and <= '9')
                return false;

            // ASCII letters, digits and underscore only. char.IsLetterOrDigit would admit
            // non-ASCII letters, which cannot appear unquoted.
            foreach (var c in name)
                if (c is (< 'A' or > 'Z') and (< 'a' or > 'z') and (< '0' or > '9') and not '_')
                    return false;

            return ReservedWords.Contains(name) == false;
        }

        /// <summary>
        /// Appends a property access to <paramref name="builder"/>, choosing dot or bracket
        /// notation as required by <paramref name="name"/>.
        /// </summary>
        /// <param name="builder">The buffer to append to.</param>
        /// <param name="name">The property name.</param>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="name"/> is <c>null</c>.</exception>
        public static void WritePropertyAccess(StringBuilder builder, string name)
        {
            if (builder is null)
                throw new ArgumentNullException(nameof(builder));
            if (name is null)
                throw new ArgumentNullException(nameof(name));

            if (IsBareIdentifier(name))
            {
                builder.Append('.').Append(name);
            }
            else
            {
                builder.Append('[');
                WriteStringLiteral(builder, name);
                builder.Append(']');
            }
        }

        /// <summary>
        /// Appends an array index access to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The buffer to append to.</param>
        /// <param name="index">The zero-based array index.</param>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
        public static void WriteIndexAccess(StringBuilder builder, int index)
        {
            if (builder is null)
                throw new ArgumentNullException(nameof(builder));
            if (index < 0)
                throw new ArgumentOutOfRangeException(nameof(index));

            builder.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
        }

        /// <summary>
        /// Appends a double-quoted, JSON-escaped string literal to <paramref name="builder"/>.
        /// </summary>
        /// <param name="builder">The buffer to append to.</param>
        /// <param name="value">The string value.</param>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> or <paramref name="value"/> is <c>null</c>.</exception>
        public static void WriteStringLiteral(StringBuilder builder, string value)
        {
            if (builder is null)
                throw new ArgumentNullException(nameof(builder));
            if (value is null)
                throw new ArgumentNullException(nameof(value));

            builder.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '\b':
                        builder.Append("\\b");
                        break;
                    case '\f':
                        builder.Append("\\f");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            builder.Append(c);
                        break;
                }
            }

            builder.Append('"');
        }

        /// <summary>
        /// Appends a JSON literal for <paramref name="value"/> to <paramref name="builder"/>.
        /// </summary>
        /// <remarks>
        /// Intended for planner-generated constants such as fetch counts and rewritten literals.
        /// Values originating from user input should be bound through <see cref="CosmosParameterList"/>
        /// instead of inlined.
        /// </remarks>
        /// <param name="builder">The buffer to append to.</param>
        /// <param name="value">The value to render. <c>null</c> renders as the JSON <c>null</c> literal.</param>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <c>null</c>.</exception>
        /// <exception cref="NotSupportedException"><paramref name="value"/> has no JSON representation.</exception>
        public static void WriteLiteral(StringBuilder builder, object? value)
        {
            if (builder is null)
                throw new ArgumentNullException(nameof(builder));

            switch (value)
            {
                case null:
                    builder.Append("null");
                    break;
                case bool b:
                    builder.Append(b ? "true" : "false");
                    break;
                case string s:
                    WriteStringLiteral(builder, s);
                    break;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    break;
                case float f:
                    WriteDouble(builder, f);
                    break;
                case double d:
                    WriteDouble(builder, d);
                    break;
                case decimal m:
                    builder.Append(m.ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    throw new NotSupportedException($"No Cosmos SQL literal representation for '{value.GetType()}'.");
            }
        }

        /// <summary>
        /// Appends a round-trippable JSON number for a floating point value.
        /// </summary>
        static void WriteDouble(StringBuilder builder, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new NotSupportedException("NaN and infinity have no Cosmos SQL literal representation.");

            builder.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

    }

}
