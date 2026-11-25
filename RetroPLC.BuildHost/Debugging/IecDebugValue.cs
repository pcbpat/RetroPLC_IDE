// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using static System.Buffers.Binary.BinaryPrimitives;

namespace RetroPLC.BuildHost.Debugging;

public static class IecDebugValue
{
    public const string Invalid = "<invalid>";
    public const string Unsupported = "<unsupported>";
    private const long NanosecondsPerSecond = 1_000_000_000;
    private const long NanosecondsPerDay = 86_400 * NanosecondsPerSecond;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16 = new UnicodeEncoding(false, false, true);

    public static string Decode(DebugSymbol symbol, ReadOnlySpan<byte> data)
    {
        var width = symbol.DataType switch
        {
            "BOOL" or "SINT" or "USINT" or "BYTE" => 1,
            "INT" or "UINT" or "WORD" => 2,
            "DINT" or "UDINT" or "DWORD" or "REAL" => 4,
            "LINT" or "ULINT" or "LWORD" or "LREAL" or "TIME" or "DATE" or "TOD" or "DT" => 8,
            "STRING" => 127,
            "WSTRING" => 253,
            _ => 0
        };
        if (width == 0)
            return Unsupported;
        if (data.Length != width || symbol.Size != width)
            return Invalid;

        try
        {
            return symbol.DataType switch
            {
                "BOOL" => data[0] switch { 0 => "FALSE", 1 => "TRUE", _ => Invalid },
                "SINT" => unchecked((sbyte)data[0]).ToString(Invariant),
                "USINT" or "BYTE" => data[0].ToString(Invariant),
                "INT" => ReadInt16LittleEndian(data).ToString(Invariant),
                "UINT" or "WORD" => ReadUInt16LittleEndian(data).ToString(Invariant),
                "DINT" => ReadInt32LittleEndian(data).ToString(Invariant),
                "UDINT" or "DWORD" => ReadUInt32LittleEndian(data).ToString(Invariant),
                "LINT" => ReadInt64LittleEndian(data).ToString(Invariant),
                "ULINT" or "LWORD" => ReadUInt64LittleEndian(data).ToString(Invariant),
                "REAL" => ReadSingleLittleEndian(data).ToString("R", Invariant),
                "LREAL" => ReadDoubleLittleEndian(data).ToString("R", Invariant),
                "TIME" => FormattableString.Invariant($"T#{ReadInt64LittleEndian(data)}ns"),
                "DATE" => FormatDate(ReadInt64LittleEndian(data)),
                "TOD" => FormatTimeOfDay(ReadInt64LittleEndian(data)),
                "DT" => FormatDateTime(ReadInt64LittleEndian(data)),
                "STRING" when data[0] <= 126 => Utf8.GetString(data.Slice(1, data[0])),
                "WSTRING" when data[0] <= 126 => Utf16.GetString(data.Slice(1, 2 * data[0])),
                _ => Invalid
            };
        }
        catch (Exception error) when (error is ArgumentOutOfRangeException or DecoderFallbackException or OverflowException)
        {
            return Invalid;
        }
    }

    private static string FormatDate(long days) =>
        "D#" + DateTime.UnixEpoch.AddTicks(checked(days * TimeSpan.TicksPerDay)).ToString("yyyy-MM-dd", Invariant);

    private static string FormatTimeOfDay(long nanoseconds) =>
        nanoseconds is >= 0 and < NanosecondsPerDay
            ? "TOD#" + DateTime.UnixEpoch.AddTicks(nanoseconds / 100).ToString("HH:mm:ss", Invariant) +
              Fraction(nanoseconds % NanosecondsPerSecond)
            : Invalid;

    private static string FormatDateTime(long nanoseconds)
    {
        var (seconds, fraction) = Math.DivRem(nanoseconds, NanosecondsPerSecond);
        if (fraction < 0)
        {
            seconds--;
            fraction += NanosecondsPerSecond;
        }

        return "DT#" + DateTime.UnixEpoch.AddSeconds(seconds).ToString("yyyy-MM-dd-HH:mm:ss", Invariant) +
               Fraction(fraction);
    }

    private static string Fraction(long nanoseconds) =>
        nanoseconds == 0 ? string.Empty : "." + nanoseconds.ToString("D9", Invariant).TrimEnd('0');
}
