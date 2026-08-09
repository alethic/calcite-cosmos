using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Apache.Calcite.Cosmos.Adapter.Client
{

    /// <summary>
    /// Builds the JSON document a row describes, and reads back the values a write request needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The reverse of <see cref="CosmosJson"/>, and it has one more problem than the reading side: a row
    /// arriving here can hold either a Java box or a CLR primitive. The reading side produces Java boxes
    /// throughout, because everything above it is compiled Java; but a literal in a <c>VALUES</c> is
    /// compiled by the CLR conventions and arrives as a CLR value. Both are accepted, and anything else
    /// is refused rather than stringified.
    /// </para>
    /// <para>
    /// <b>The map column is the document and a promoted column sets a property of it.</b> A promoted
    /// column contributes only where its value is not null, because an unmentioned column arrives as
    /// null and the alternative would overwrite the document it was handed with a row of nulls. The
    /// consequence — that a promoted column cannot write a JSON null — is recorded in <c>DESIGN.md</c>
    /// under <em>What an insert writes</em>; the map column can, a map distinguishing an absent key
    /// from one holding null.
    /// </para>
    /// </remarks>
    public static class CosmosDocument
    {

        /// <summary>
        /// The properties the service owns, which are never written whatever a row carries.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>_ts</c> and <c>_etag</c> cannot be named in an <c>INSERT</c> — they are declared
        /// <c>STORED</c>, so the validator refuses — but they can still arrive <em>inside</em> the map
        /// column, which is what <c>INSERT INTO t (_MAP) SELECT "_MAP" FROM t2</c> hands over: a whole
        /// document, system properties and all. Copying one document to another is the obvious use of
        /// that statement, and the service's bookkeeping is not part of what is being copied.
        /// </para>
        /// <para>
        /// <b>This is a decision rather than a requirement, and it was measured.</b> With the stripping
        /// removed, a document carrying a bogus <c>_ts</c>, <c>_etag</c> and <c>_rid</c> was still
        /// accepted and still came back with values the service had assigned — so the service does not
        /// need to be protected from these. What stripping buys is that the document written is the
        /// document described: a new item does not silently carry another item's identity, and a caller
        /// reading back what they inserted is not told a value they never supplied was theirs. The
        /// measurement was against the emulator, which has disagreed with Azure in both directions
        /// before.
        /// </para>
        /// </remarks>
        public static readonly string[] ServiceProperties =
        [
            "_ts",
            "_etag",
            "_rid",
            "_self",
            "_attachments",
        ];

        /// <summary>
        /// Determines whether a property belongs to the service rather than to the document.
        /// </summary>
        /// <param name="name">The property name.</param>
        /// <returns><c>true</c> if the service owns it.</returns>
        public static bool IsServiceProperty(string name)
        {
            foreach (var reserved in ServiceProperties)
                if (string.Equals(name, reserved, StringComparison.Ordinal))
                    return true;

            return false;
        }

        /// <summary>
        /// Builds the document a row describes.
        /// </summary>
        /// <param name="columnNames">The row's field names, the map column first.</param>
        /// <param name="values">The row's values, in the same order.</param>
        /// <returns>The document, as UTF-8 JSON.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="columnNames"/> or <paramref name="values"/> is <c>null</c>.</exception>
        /// <exception cref="CosmosExecutionException">A value has no JSON counterpart, or the map column does not hold a map.</exception>
        public static byte[] Build(IReadOnlyList<string> columnNames, IReadOnlyList<object?> values)
        {
            if (columnNames is null)
                throw new ArgumentNullException(nameof(columnNames));
            if (values is null)
                throw new ArgumentNullException(nameof(values));

            // Insertion-ordered, and the map's own properties go in first so that a promoted column
            // naming one of them replaces it in place rather than appending a second.
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            var order = new List<string>();

            void Set(string name, object? value)
            {
                if (IsServiceProperty(name))
                    return;

                if (properties.ContainsKey(name) == false)
                    order.Add(name);

                properties[name] = value;
            }

            for (var i = 0; i < columnNames.Count && i < values.Count; i++)
            {
                var value = values[i];

                if (i == CosmosImplementor.MapColumnOrdinal)
                {
                    if (value is null)
                        continue;

                    if (value is not java.util.Map map)
                        throw new CosmosExecutionException($"The '{CosmosImplementor.MapColumnName}' column holds a {value.GetType().Name} rather than a map, so it does not describe a document.");

                    var entries = map.entrySet().iterator();
                    while (entries.hasNext())
                    {
                        var entry = (java.util.Map.Entry)entries.next();
                        var key = entry.getKey();

                        if (key is not string name)
                            throw new CosmosExecutionException($"A document property is keyed by a {key?.GetType().Name ?? "null"} rather than a string.");

                        Set(name, entry.getValue());
                    }

                    continue;
                }

                // A promoted column says nothing where it is null: that is how an unmentioned column
                // arrives, and it must not overwrite what the map column supplied.
                if (value is not null)
                    Set(columnNames[i], value);
            }

            var buffer = new System.Buffers.ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();

                foreach (var name in order)
                {
                    writer.WritePropertyName(name);
                    WriteValue(writer, properties[name]);
                }

                writer.WriteEndObject();
            }

            return buffer.WrittenSpan.ToArray();
        }

        /// <summary>
        /// Writes one value as JSON.
        /// </summary>
        /// <remarks>
        /// Both representations are accepted for each JSON type — the Java box a read produced and the
        /// CLR primitive a literal compiles to. A type with no JSON counterpart is refused rather than
        /// rendered as text, because a document that silently holds <c>"System.Guid"</c> is worse than a
        /// write that failed.
        /// </remarks>
        /// <param name="writer">The writer.</param>
        /// <param name="value">The value.</param>
        /// <exception cref="CosmosExecutionException">The value has no JSON counterpart.</exception>
        public static void WriteValue(Utf8JsonWriter writer, object? value)
        {
            if (writer is null)
                throw new ArgumentNullException(nameof(writer));

            switch (value)
            {
                case null:
                    writer.WriteNullValue();
                    return;

                // One case, not two: IKVM maps java.lang.String onto System.String, so a string read
                // from a document and a string compiled from a literal are the same object.
                case string s:
                    writer.WriteStringValue(s);
                    return;

                case bool b:
                    writer.WriteBooleanValue(b);
                    return;
                case java.lang.Boolean jb:
                    writer.WriteBooleanValue(jb.booleanValue());
                    return;

                case sbyte or short or int or long:
                    writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    return;
                case byte or ushort or uint:
                    writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                    return;
                case java.lang.Byte or java.lang.Short or java.lang.Integer or java.lang.Long:
                    writer.WriteNumberValue(((java.lang.Number)value).longValue());
                    return;

                case float or double:
                    writer.WriteNumberValue(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                    return;
                case java.lang.Float or java.lang.Double:
                    writer.WriteNumberValue(((java.lang.Number)value).doubleValue());
                    return;

                case decimal d:
                    writer.WriteNumberValue(d);
                    return;
                case java.math.BigDecimal bd:
                    // Written from the digits rather than through a double, which is the point of a
                    // decimal: what it carried is what it keeps.
                    writer.WriteRawValue(bd.toPlainString(), skipInputValidation: false);
                    return;
                case java.math.BigInteger bi:
                    writer.WriteRawValue(bi.toString(), skipInputValidation: false);
                    return;

                // JSON has no binary type; base64 in a string is what Cosmos and every JSON API use,
                // and is what the reading side expects to find.
                case org.apache.calcite.avatica.util.ByteString bytes:
                    writer.WriteStringValue(Convert.ToBase64String(bytes.getBytes()));
                    return;
                case byte[] raw:
                    writer.WriteStringValue(Convert.ToBase64String(raw));
                    return;

                case java.util.Map map:
                    {
                        writer.WriteStartObject();

                        var entries = map.entrySet().iterator();
                        while (entries.hasNext())
                        {
                            var entry = (java.util.Map.Entry)entries.next();

                            if (entry.getKey() is not string name)
                                throw new CosmosExecutionException($"A document property is keyed by a {entry.getKey()?.GetType().Name ?? "null"} rather than a string.");

                            writer.WritePropertyName(name);
                            WriteValue(writer, entry.getValue());
                        }

                        writer.WriteEndObject();
                        return;
                    }

                case java.util.Collection collection:
                    {
                        writer.WriteStartArray();

                        var elements = collection.iterator();
                        while (elements.hasNext())
                            WriteValue(writer, elements.next());

                        writer.WriteEndArray();
                        return;
                    }

                default:
                    throw new CosmosExecutionException($"A value of type '{value.GetType().FullName}' has no Cosmos JSON representation and cannot be written.");
            }
        }

        /// <summary>
        /// Reads the value at a policy path out of a document.
        /// </summary>
        /// <remarks>
        /// Used to recover the partition key and the <c>id</c> from the document a row describes, which
        /// is why it walks rather than naming a property: a partition key path may be nested, and a
        /// nested path is not promoted to a column.
        /// </remarks>
        /// <param name="document">The document.</param>
        /// <param name="policyPath">The path in policy form, such as <c>/inventory/sku</c>.</param>
        /// <returns>The value as a CLR primitive, or <c>null</c> where the path is absent.</returns>
        public static object? Read(JsonElement document, string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return null;

            var value = document;

            foreach (var segment in policyPath.Split('/'))
            {
                if (segment.Length == 0)
                    continue;

                if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty(segment, out value) == false)
                    return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => value.GetDouble(),
                // Null is a partition key value in Cosmos and is distinct from an absent one, which is
                // the "none" logical partition. Undefined is the absence.
                JsonValueKind.Null => null,
                _ => null,
            };
        }

        /// <summary>
        /// Determines whether a path is present in a document, distinguishing absence from a null value.
        /// </summary>
        /// <param name="document">The document.</param>
        /// <param name="policyPath">The path in policy form.</param>
        /// <returns><c>true</c> if the path is present, whatever it holds.</returns>
        public static bool Contains(JsonElement document, string policyPath)
        {
            if (string.IsNullOrEmpty(policyPath))
                return false;

            var value = document;

            foreach (var segment in policyPath.Split('/'))
            {
                if (segment.Length == 0)
                    continue;

                if (value.ValueKind != JsonValueKind.Object || value.TryGetProperty(segment, out value) == false)
                    return false;
            }

            return true;
        }

    }

}
