using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

using Apache.Calcite.Cosmos.Adapter.Sql;

using org.apache.calcite.sql.type;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Reads the JSON value Cosmos returns for a row into the representation Calcite holds a value of
    /// that SQL type in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values are Java boxes rather than CLR primitives — <see cref="java.lang.Integer"/>, not
    /// <see cref="int"/> — because everything above this reads them through Calcite's own operators,
    /// which are compiled Java. A CLR <see cref="int"/> in a row would compile and then fail at the
    /// first operator that casts. Calcite's internal encodings apply too: a <c>DATE</c> is a day count
    /// and a <c>TIME</c> a millisecond-of-day, both as <see cref="java.lang.Integer"/>.
    /// </para>
    /// <para>
    /// A property Cosmos does not return is absent from the object rather than present and null — the
    /// service elides <c>undefined</c>. Both read as SQL <c>NULL</c> here, which is the only available
    /// reading: the map column aside, nothing in this adapter can distinguish a missing property from a
    /// null one, and SQL has no third value to distinguish them with.
    /// </para>
    /// </remarks>
    public static class CosmosJson
    {

        /// <summary>
        /// The day <see cref="SqlTypeName.DATE"/> counts from.
        /// </summary>
        static readonly DateOnly UnixEpochDay = new(1970, 1, 1);

        /// <summary>
        /// Reads a named property of a row as a value of the given SQL type.
        /// </summary>
        /// <param name="row">The JSON value Cosmos returned for the row.</param>
        /// <param name="name">The property to read.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <returns>The value, or <c>null</c> where the property is absent or JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The value cannot be read as that type.</exception>
        public static object? GetProperty(JsonElement row, string name, SqlTypeName typeName)
        {
            // A row that is not an object has no properties to name. That is not reachable through the
            // converters, which always project an object, but it is worth failing loudly rather than
            // silently yielding a row of nulls if one ever stops doing so.
            if (row.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for the row, got {row.ValueKind}.");

            return row.TryGetProperty(name, out var value) ? GetValue(value, typeName) : null;
        }

        /// <summary>
        /// Reads a document path as a value of the given SQL type.
        /// </summary>
        /// <remarks>
        /// What a point read needs, and what the projected form does not: the row is the document
        /// itself, so a field is reached by walking its path rather than by naming a property of a
        /// constructed object. An empty path is the document — the map column.
        /// <para>
        /// A segment that is not there reads as SQL <c>NULL</c>, exactly as an absent property does in
        /// the projected form, because both are Cosmos returning nothing for that path.
        /// </para>
        /// </remarks>
        /// <param name="document">The document.</param>
        /// <param name="segments">The path's segments, relative to the document root.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <returns>The value, or <c>null</c> where the path is absent or JSON null.</returns>
        public static object? GetPath(JsonElement document, IReadOnlyList<CosmosPathSegment> segments, SqlTypeName typeName)
        {
            if (segments is null)
                throw new ArgumentNullException(nameof(segments));

            var value = document;

            foreach (var segment in segments)
            {
                if (segment.IsIndex)
                {
                    if (value.ValueKind != JsonValueKind.Array || segment.ArrayIndex >= value.GetArrayLength())
                        return null;

                    value = value[segment.ArrayIndex];
                    continue;
                }

                if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty(segment.Name!, out value) == false)
                    return null;
            }

            return GetValue(value, typeName);
        }

        /// <summary>
        /// Reads a JSON value as a value of the given SQL type.
        /// </summary>
        /// <param name="value">The value to read.</param>
        /// <param name="typeName">The SQL type the plan was built against.</param>
        /// <returns>The value, or <c>null</c> where the value is JSON null.</returns>
        /// <exception cref="CosmosMaterializationException">The value cannot be read as that type.</exception>
        public static object? GetValue(JsonElement value, SqlTypeName typeName)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            // Dispatched on the name: a Java enum's ordinals are not stable across versions, its names are.
            switch (typeName?.name())
            {
                case nameof(SqlTypeName.NULL):
                    return null;
                case nameof(SqlTypeName.BOOLEAN):
                    return java.lang.Boolean.valueOf(GetBoolean(value));
                case nameof(SqlTypeName.TINYINT):
                    // A Java byte is signed and IKVM surfaces it as a CLR byte, so the range is checked
                    // against sbyte and the bit pattern reinterpreted rather than clamped.
                    return java.lang.Byte.valueOf(unchecked((byte)(sbyte)GetInteger(value, sbyte.MinValue, sbyte.MaxValue, typeName)));
                case nameof(SqlTypeName.SMALLINT):
                    return java.lang.Short.valueOf((short)GetInteger(value, short.MinValue, short.MaxValue, typeName));
                case nameof(SqlTypeName.INTEGER):
                    return java.lang.Integer.valueOf((int)GetInteger(value, int.MinValue, int.MaxValue, typeName));
                case nameof(SqlTypeName.BIGINT):
                    return java.lang.Long.valueOf(GetInteger(value, long.MinValue, long.MaxValue, typeName));
                case nameof(SqlTypeName.REAL):
                    return java.lang.Float.valueOf((float)GetDouble(value));
                case nameof(SqlTypeName.FLOAT):
                case nameof(SqlTypeName.DOUBLE):
                    return java.lang.Double.valueOf(GetDouble(value));
                case nameof(SqlTypeName.DECIMAL):
                    return GetDecimal(value);
                case nameof(SqlTypeName.CHAR):
                case nameof(SqlTypeName.VARCHAR):
                    return GetString(value);
                case nameof(SqlTypeName.BINARY):
                case nameof(SqlTypeName.VARBINARY):
                    return GetBinary(value);
                case nameof(SqlTypeName.DATE):
                    return java.lang.Integer.valueOf(GetDate(value));
                case nameof(SqlTypeName.TIME):
                    return java.lang.Integer.valueOf(GetTime(value));
                case nameof(SqlTypeName.TIMESTAMP):
                case nameof(SqlTypeName.TIMESTAMP_TZ):
                    return java.lang.Long.valueOf(GetTimestamp(value));
                case nameof(SqlTypeName.MAP):
                    return GetMap(value);
                case nameof(SqlTypeName.ARRAY):
                case nameof(SqlTypeName.MULTISET):
                    return GetList(value);
                case nameof(SqlTypeName.ANY):
                case nameof(SqlTypeName.OTHER):
                    return GetNatural(value);
                default:
                    throw new CosmosMaterializationException($"No Cosmos JSON reading is defined for SQL type '{typeName?.name() ?? "null"}'.");
            }
        }

        /// <summary>
        /// Reads a value by its JSON type rather than by a declared SQL type.
        /// </summary>
        /// <remarks>
        /// What a <c>MAP</c>, an <c>ARRAY</c> and an <c>ANY</c> hold. A container has no row schema, so a
        /// document's shape is discovered here and nowhere earlier — which is exactly why these three
        /// types exist in the row model at all.
        /// </remarks>
        /// <param name="value">The value to read.</param>
        /// <returns>The value.</returns>
        public static object? GetNatural(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.True:
                    return java.lang.Boolean.TRUE;
                case JsonValueKind.False:
                    return java.lang.Boolean.FALSE;
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Object:
                    return GetMap(value);
                case JsonValueKind.Array:
                    return GetList(value);
                case JsonValueKind.Number:
                    // JSON numbers are IEEE doubles in Cosmos, but an integral one arrives as a Long so
                    // that an identifier or a count does not surface as 42.0. The choice is the value's,
                    // not the schema's, there being no schema to consult.
                    return value.TryGetInt64(out var integral)
                        ? java.lang.Long.valueOf(integral)
                        : java.lang.Double.valueOf(value.GetDouble());
                default:
                    throw new CosmosMaterializationException($"Unexpected JSON value kind '{value.ValueKind}'.");
            }
        }

        /// <summary>
        /// Reads a JSON object as the <see cref="java.util.Map"/> a <c>MAP</c> column holds.
        /// </summary>
        /// <param name="value">The value to read.</param>
        /// <returns>The map.</returns>
        public static java.util.Map GetMap(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Object)
                throw new CosmosMaterializationException($"Expected a JSON object for a MAP, got {value.ValueKind}.");

            // Insertion-ordered, so that a map rendered back to text reads in document order.
            var map = new java.util.LinkedHashMap();

            foreach (var property in value.EnumerateObject())
                map.put(property.Name, GetNatural(property.Value));

            return map;
        }

        /// <summary>
        /// Reads a JSON array as the <see cref="java.util.List"/> an <c>ARRAY</c> column holds.
        /// </summary>
        /// <param name="value">The value to read.</param>
        /// <returns>The list.</returns>
        public static java.util.List GetList(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw new CosmosMaterializationException($"Expected a JSON array for an ARRAY, got {value.ValueKind}.");

            var list = new java.util.ArrayList(value.GetArrayLength());

            foreach (var element in value.EnumerateArray())
                list.add(GetNatural(element));

            return list;
        }

        static bool GetBoolean(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new CosmosMaterializationException($"Expected a JSON boolean, got {value.ValueKind}."),
        };

        static string GetString(JsonElement value)
        {
            // Only a JSON string reads as a character value. Cosmos is schemaless and a property may hold
            // a number where the plan expected text; coercing it would make the row type a suggestion.
            if (value.ValueKind != JsonValueKind.String)
                throw new CosmosMaterializationException($"Expected a JSON string, got {value.ValueKind}.");

            return value.GetString()!;
        }

        static double GetDouble(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number)
                throw new CosmosMaterializationException($"Expected a JSON number, got {value.ValueKind}.");

            return value.GetDouble();
        }

        static long GetInteger(JsonElement value, long min, long max, SqlTypeName typeName)
        {
            if (value.ValueKind != JsonValueKind.Number)
                throw new CosmosMaterializationException($"Expected a JSON number, got {value.ValueKind}.");

            // A Cosmos number is a double, so an integral column legitimately arrives written as 42.0 —
            // which TryGetInt64 rejects, integer syntax being what it tests. Falling back to the double
            // accepts that, and still refuses 42.5: rounding would answer a question the query did not ask.
            if (value.TryGetInt64(out var integral) == false)
            {
                var number = value.GetDouble();

                if (double.IsFinite(number) == false || Math.Floor(number) != number)
                    throw new CosmosMaterializationException($"A JSON number of '{value.GetRawText()}' is not a whole number and cannot be read as {typeName.name()}.");

                // Checked before the cast, which would otherwise wrap rather than fail. The bounds are
                // widened to double for the comparison, so a value within one ULP of the extreme is
                // admitted here and caught by the range check below.
                if (number < min || number > max)
                    throw new CosmosMaterializationException($"A JSON number of '{value.GetRawText()}' is outside the range of {typeName.name()}.");

                integral = (long)number;
            }

            if (integral < min || integral > max)
                throw new CosmosMaterializationException($"A JSON number of '{value.GetRawText()}' is outside the range of {typeName.name()}.");

            return integral;
        }

        static java.math.BigDecimal GetDecimal(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.Number)
                throw new CosmosMaterializationException($"Expected a JSON number, got {value.ValueKind}.");

            // Built from the raw text rather than from a double, which is the whole point of a decimal:
            // the digits JSON carried are the digits the value keeps.
            return new java.math.BigDecimal(value.GetRawText());
        }

        static org.apache.calcite.avatica.util.ByteString GetBinary(JsonElement value)
        {
            // JSON has no binary type; base64 in a string is the encoding Cosmos and every JSON API use.
            if (value.ValueKind != JsonValueKind.String)
                throw new CosmosMaterializationException($"Expected a base64 JSON string for a binary value, got {value.ValueKind}.");

            if (value.TryGetBytesFromBase64(out var bytes) == false)
                throw new CosmosMaterializationException("A binary value is not valid base64.");

            return new org.apache.calcite.avatica.util.ByteString(bytes);
        }

        static int GetDate(JsonElement value)
        {
            // A whole number is already the day count Calcite wants; a string is the ISO-8601 date Cosmos
            // documents as the way to store one.
            if (value.ValueKind == JsonValueKind.Number)
                return (int)GetInteger(value, int.MinValue, int.MaxValue, SqlTypeName.DATE);

            return DateOnly.FromDateTime(GetDateTime(value)).DayNumber - UnixEpochDay.DayNumber;
        }

        static int GetTime(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number)
                return (int)GetInteger(value, int.MinValue, int.MaxValue, SqlTypeName.TIME);

            return (int)GetDateTime(value).TimeOfDay.TotalMilliseconds;
        }

        static long GetTimestamp(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Number)
                return GetInteger(value, long.MinValue, long.MaxValue, SqlTypeName.TIMESTAMP);

            return new DateTimeOffset(DateTime.SpecifyKind(GetDateTime(value), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        }

        static DateTime GetDateTime(JsonElement value)
        {
            if (value.ValueKind != JsonValueKind.String)
                throw new CosmosMaterializationException($"Expected a JSON string or number for a temporal value, got {value.ValueKind}.");

            var text = value.GetString()!;

            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) == false)
                throw new CosmosMaterializationException($"A temporal value of '{text}' is not an ISO-8601 date or time.");

            return parsed;
        }

    }

}
