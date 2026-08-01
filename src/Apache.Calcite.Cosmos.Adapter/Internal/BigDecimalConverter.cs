using System;
using System.Buffers.Binary;

namespace Apache.Calcite.Cosmos.Adapter.Internal
{

    /// <summary>
    /// Lossless binary conversion from <see cref="java.math.BigDecimal"/> to <see cref="decimal"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both types store an integer mantissa plus a non-negative scale (number of decimal digits to
    /// the right of the point). <see cref="decimal"/> uses a 96-bit unsigned mantissa with scale
    /// 0..28; <see cref="java.math.BigDecimal"/> uses an arbitrary-precision
    /// <see cref="java.math.BigInteger"/> mantissa with a signed 32-bit scale. The mantissa is
    /// transferred as raw bytes through <see cref="java.math.BigInteger"/>'s two's-complement byte
    /// representation, avoiding any string round-trip.
    /// </para>
    /// <para>
    /// Ported from the equivalent converter in the <c>calcite-dotnet</c> repository, where it is
    /// internal to the ADO.NET provider. Only the inbound direction is needed here: parameter
    /// values flow out to Cosmos as CLR types and never return as <see cref="java.math.BigDecimal"/>.
    /// </para>
    /// </remarks>
    internal static class BigDecimalConverter
    {

        /// <summary>
        /// Converts a <see cref="java.math.BigDecimal"/> to a <see cref="decimal"/>.
        /// </summary>
        /// <param name="value">The value to convert.</param>
        /// <returns>The converted value.</returns>
        /// <exception cref="OverflowException">The magnitude exceeds the range of <see cref="decimal"/>.</exception>
        public static decimal ToDecimal(java.math.BigDecimal value)
        {
            // System.Decimal requires scale in [0, 28]; normalize first.
            var scale = value.scale();
            if (scale > 28)
                value = value.setScale(28, java.math.RoundingMode.HALF_EVEN);
            else if (scale < 0)
                value = value.setScale(0);

            scale = value.scale();
            var unscaled = value.unscaledValue();
            var sign = unscaled.signum();
            if (sign == 0)
                return 0m;

            var abs = unscaled.abs();
            if (abs.bitLength() > 96)
                throw new OverflowException("BigDecimal magnitude exceeds System.Decimal range.");

            // BigInteger.toByteArray() is signed big-endian two's complement; for the absolute
            // value it may include a leading zero byte. Right-align into a 12-byte stack buffer.
            var bytes = abs.toByteArray();
            Span<byte> mag = stackalloc byte[12];
            mag.Clear();
            var src = bytes.AsSpan();
            if (src.Length > 12)
                src = src.Slice(src.Length - 12);
            src.CopyTo(mag.Slice(12 - src.Length));

            var hi = BinaryPrimitives.ReadInt32BigEndian(mag);
            var mid = BinaryPrimitives.ReadInt32BigEndian(mag.Slice(4));
            var lo = BinaryPrimitives.ReadInt32BigEndian(mag.Slice(8));

            return new decimal(lo, mid, hi, sign < 0, (byte)scale);
        }

    }

}
