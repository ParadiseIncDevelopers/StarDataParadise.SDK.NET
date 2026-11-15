using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
#if NETFRAMEWORK
using DateOnly = System.DateTime;
#endif

namespace StarDataParadise.SDK;

/// <summary>An exact SQL decimal, including its coefficient and declared scale.</summary>
public readonly record struct SdpDecimal
{
    private static readonly BigInteger CoefficientLimit = BigInteger.Pow(10, 38);

    public SdpDecimal(BigInteger unscaled, int scale)
    {
        if (scale is < 0 or > 38)
            throw new ArgumentOutOfRangeException(nameof(scale));
        if (BigInteger.Abs(unscaled) >= CoefficientLimit)
            throw new ArgumentOutOfRangeException(nameof(unscaled), "A SQL decimal has at most 38 coefficient digits.");
        Unscaled = unscaled;
        Scale = scale;
    }

    public BigInteger Unscaled { get; }
    public int Scale { get; }

    public static SdpDecimal FromDecimal(decimal value)
    {
        var bits = decimal.GetBits(value);
        var coefficient = (BigInteger)(uint)bits[0] | (BigInteger)(uint)bits[1] << 32 |
            (BigInteger)(uint)bits[2] << 64;
        return new SdpDecimal(bits[3] < 0 ? -coefficient : coefficient, (bits[3] >> 16) & 0xff);
    }

    public decimal ToDecimal()
    {
        var magnitude = BigInteger.Abs(Unscaled);
        if (Scale > 28 || magnitude >= (BigInteger.One << 96))
            throw new OverflowException("SQL decimal cannot be represented exactly as System.Decimal.");
        return new decimal(
            unchecked((int)(uint)(magnitude & uint.MaxValue)),
            unchecked((int)(uint)((magnitude >> 32) & uint.MaxValue)),
            unchecked((int)(uint)(magnitude >> 64)),
            Unscaled.Sign < 0,
            (byte)Scale);
    }

    public override string ToString()
    {
        var digits = BigInteger.Abs(Unscaled).ToString(CultureInfo.InvariantCulture);
        if (Scale > 0)
        {
            digits = digits.PadLeft(Scale + 1, '0');
            digits = digits.Insert(digits.Length - Scale, ".");
        }
        return Unscaled.Sign < 0 ? "-" + digits : digits;
    }
}

/// <summary>A civil SQL TIME with nanosecond precision.</summary>
public readonly record struct SdpTime
{
    public SdpTime(int hour, int minute, int second, int nanos = 0)
    {
        if (hour is < 0 or > 23)
            throw new ArgumentOutOfRangeException(nameof(hour));
        if (minute is < 0 or > 59)
            throw new ArgumentOutOfRangeException(nameof(minute));
        if (second is < 0 or > 59)
            throw new ArgumentOutOfRangeException(nameof(second));
        if (nanos is < 0 or > 999_999_999)
            throw new ArgumentOutOfRangeException(nameof(nanos));
        (Hour, Minute, Second, Nanos) = (hour, minute, second, nanos);
    }

    public int Hour { get; }
    public int Minute { get; }
    public int Second { get; }
    public int Nanos { get; }

#if !NETFRAMEWORK
    public static SdpTime FromTimeOnly(TimeOnly value) =>
        new(value.Hour, value.Minute, value.Second, (int)(value.Ticks % TimeSpan.TicksPerSecond) * 100);

    public TimeOnly ToTimeOnly()
    {
        if (Nanos % 100 != 0)
            throw new OverflowException("SQL time cannot be represented exactly as TimeOnly.");
        return new TimeOnly(new TimeOnly(Hour, Minute, Second).Ticks + Nanos / 100);
    }

#endif

    public override string ToString() =>
        FormattableString.Invariant($"{Hour:00}:{Minute:00}:{Second:00}.{Nanos:000000000}");

    internal static SdpTime Parse(string value)
    {
        if (value.Length is < 8 or 9 or > 18 || value[2] != ':' || value[5] != ':' ||
            (value.Length > 8 && value[8] != '.'))
            throw new FormatException("Invalid SQL time.");
        var nanos = 0;
        if (value.Length > 8)
        {
            nanos = Digits(value.AsSpan(9));
            for (var digits = value.Length - 9; digits < 9; digits++)
                nanos *= 10;
        }
        return new SdpTime(Digits(value.AsSpan(0, 2)), Digits(value.AsSpan(3, 2)), Digits(value.AsSpan(6, 2)), nanos);
    }

    private static int Digits(ReadOnlySpan<char> text) =>
        int.Parse(text.ToString(), NumberStyles.None, CultureInfo.InvariantCulture);
}

/// <summary>A wire timestamp with its original offset, optional zone and nanoseconds.</summary>
public readonly record struct SdpTimestamp
{
    public SdpTimestamp(long seconds, int nanos, int offsetMinutes, string? zone = null)
    {
        if (nanos is < 0 or > 999_999_999)
            throw new ArgumentOutOfRangeException(nameof(nanos));
        if (offsetMinutes is < -840 or > 840)
            throw new ArgumentOutOfRangeException(nameof(offsetMinutes));
        if (zone is { Length: 0 })
            throw new ArgumentException("A named zone must be nonempty.", nameof(zone));
        if (zone is not null) SdpValues.ValidateText(zone);
        (Seconds, Nanos, OffsetMinutes, Zone) = (seconds, nanos, offsetMinutes, zone);
    }

    public long Seconds { get; }
    public int Nanos { get; }
    public int OffsetMinutes { get; }
    public string? Zone { get; }

    public static SdpTimestamp FromDateTimeOffset(DateTimeOffset value) =>
        new(value.ToUnixTimeSeconds(), (int)(value.UtcDateTime.Ticks % TimeSpan.TicksPerSecond) * 100, (int)value.Offset.TotalMinutes);

    public DateTimeOffset ToDateTimeOffset()
    {
        if (Nanos % 100 != 0 || Zone is not null)
            throw new OverflowException("SQL timestamp cannot be represented exactly as DateTimeOffset.");
        return DateTimeOffset.FromUnixTimeSeconds(Seconds).AddTicks(Nanos / 100)
            .ToOffset(TimeSpan.FromMinutes(OffsetMinutes));
    }
}

/// <summary>An ordered PL/SQL record with unique field names.</summary>
public sealed class SdpRecord : IReadOnlyList<KeyValuePair<string, object?>>
{
    private readonly KeyValuePair<string, object?>[] _fields;

    public SdpRecord(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        if (fields is null) throw new ArgumentNullException(nameof(fields));
        _fields = fields.Take(129).ToArray();
        if (_fields.Length > 128)
            throw new ArgumentException("A record supports up to 128 fields.", nameof(fields));
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in _fields)
        {
            if (string.IsNullOrEmpty(field.Key) || field.Key.Contains('\0') ||
                SdpValues.ValidateText(field.Key) > 128 || !names.Add(field.Key))
                throw new ArgumentException("Record field names must be unique and contain 1 to 128 UTF-8 bytes.", nameof(fields));
        }
    }

    public int Count => _fields.Length;
    public KeyValuePair<string, object?> this[int index] => _fields[index];
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        ((IEnumerable<KeyValuePair<string, object?>>)_fields).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>An ordered PL/SQL collection of typed values.</summary>
public sealed class SdpCollection : IReadOnlyList<object?>
{
    private readonly object?[] _items;

    public SdpCollection(IEnumerable<object?> items)
    {
        if (items is null) throw new ArgumentNullException(nameof(items));
        _items = items.Take(129).ToArray();
        if (_items.Length > 128)
            throw new ArgumentException("A collection supports up to 128 items.", nameof(items));
    }

    public int Count => _items.Length;
    public object? this[int index] => _items[index];
    public IEnumerator<object?> GetEnumerator() => ((IEnumerable<object?>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal static class SdpValues
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly object NullValue = new { t = "null" };

    internal static int ValidateText(string text) => StrictUtf8.GetByteCount(text);

    internal static string BaseType(string type) => type.StartsWith("DECIMAL(", StringComparison.Ordinal) ? "DECIMAL" : type;

    internal static Type ClrType(string type) => BaseType(type) switch
    {
        "NULL" => typeof(DBNull),
        "BOOLEAN" => typeof(bool),
        "BIGINT" => typeof(long),
        "DECIMAL" => typeof(SdpDecimal),
        "TEXT" => typeof(string),
        "BLOB" => typeof(byte[]),
        "DATE" => typeof(DateOnly),
        "TIME" => typeof(SdpTime),
        "TIMESTAMP WITH TIME ZONE" => typeof(SdpTimestamp),
        "RECORD" => typeof(SdpRecord),
        "COLLECTION" => typeof(SdpCollection),
        _ => throw new NotSupportedException("Unsupported SQL metadata type.")
    };

    internal static string InferType(object? value) => value switch
    {
        null or DBNull or string => "TEXT",
        bool => "BOOLEAN",
        sbyte or byte or short or ushort or int or uint or long or ulong => "BIGINT",
        decimal or SdpDecimal or BigInteger => "DECIMAL",
        byte[] or ReadOnlyMemory<byte> or Memory<byte> or ArraySegment<byte> => "BLOB",
#if !NETFRAMEWORK
        DateOnly => "DATE",
        TimeOnly => "TIME",
#endif
        TimeSpan or SdpTime => "TIME",
        DateTimeOffset or DateTime or SdpTimestamp => "TIMESTAMP WITH TIME ZONE",
        SdpRecord => "RECORD",
        SdpCollection => "COLLECTION",
        _ => throw new NotSupportedException("The CLR value has no exact SQL wire type.")
    };

    internal static object Decode(JsonElement wire, string? declaredType = null)
    {
        var tag = Text(wire, "t");
        if (tag == "null") return DBNull.Value;
        var declared = declaredType is null ? null : BaseType(declaredType);
        return tag switch
        {
            "bool" when Matches(declared, "BOOLEAN") => wire.GetProperty("v").GetBoolean(),
            "i64" when Matches(declared, "BIGINT") => ReadInteger(wire, "v"),
            "decimal" when Matches(declared, "DECIMAL") => ReadDecimal(wire),
            "text" when declared == "DATE" => ReadDate(wire),
            "text" when declared == "TIME" => SdpTime.Parse(Text(wire, "v")),
            "text" when Matches(declared, "TEXT") => Text(wire, "v"),
            "date" when Matches(declared, "DATE") => ReadDate(wire),
            "time" when Matches(declared, "TIME") => SdpTime.Parse(Text(wire, "v")),
            "binary" when Matches(declared, "BLOB") => ReadBinary(wire),
            "timestamp" when Matches(declared, "TIMESTAMP WITH TIME ZONE") => new SdpTimestamp(
                ReadInteger(wire, "seconds"), wire.GetProperty("nanos").GetInt32(),
                wire.GetProperty("offset_minutes").GetInt32(), wire.TryGetProperty("zone", out _) ? Text(wire, "zone") : null),
            "record" when Matches(declared, "RECORD") => new SdpRecord(wire.GetProperty("fields").EnumerateArray()
                .Select(field => new KeyValuePair<string, object?>(Text(field, "name"), Decode(field.GetProperty("value"))))),
            "collection" when Matches(declared, "COLLECTION") => new SdpCollection(wire.GetProperty("items").EnumerateArray()
                .Select(cell => Decode(cell))),
            _ => throw new JsonException("Unsupported or inconsistent SQL wire value.")
        };
    }

    internal static object Encode(string type, object? value)
    {
        var nodes = 0;
        return EncodeCell(type, value, 0);

        object EncodeCell(string declared, object? cellValue, int depth)
        {
            if (++nodes > 2048)
                throw new ArgumentException("A compound value exceeds the supported node limit.");
            if (depth >= 8 && declared is ("RECORD" or "COLLECTION"))
                throw new ArgumentException("A compound value exceeds the supported nesting limit.");
            if (cellValue is null or DBNull) return NullValue;
            return BaseType(declared) switch
            {
                "BOOLEAN" when cellValue is bool boolean => new { t = "bool", v = boolean },
                "BIGINT" => new { t = "i64", v = ToInteger(cellValue).ToString(Invariant) },
                "DECIMAL" => EncodeDecimal(ToFixed(cellValue)),
                "TEXT" when cellValue is string text => EncodeText("text", text),
                "BLOB" => new { t = "binary", hex = EncodeBinary(cellValue) },
                "DATE" => EncodeText(depth > 0 ? "date" : "text", ToDate(cellValue).ToString("yyyy-MM-dd", Invariant)),
                "TIME" => EncodeText(depth > 0 ? "time" : "text", ToTime(cellValue).ToString()),
                "TIMESTAMP WITH TIME ZONE" => EncodeTimestamp(ToTimestamp(cellValue)),
                "RECORD" when cellValue is SdpRecord record => new
                {
                    t = "record",
                    fields = record.Select(field => new
                    {
                        name = field.Key,
                        value = EncodeCell(InferType(field.Value), field.Value, depth + 1)
                    }).ToArray()
                },
                "COLLECTION" when cellValue is SdpCollection collection => new
                {
                    t = "collection", items = collection.Select(item => EncodeCell(InferType(item), item, depth + 1)).ToArray()
                },
                _ => throw new InvalidCastException("Parameter CLR value does not match its SQL type.")
            };
        }
    }

    internal static SdpDecimal ToFixed(object value) => value switch
    {
        SdpDecimal fixedPoint => fixedPoint,
        decimal number => SdpDecimal.FromDecimal(number),
        BigInteger number => new SdpDecimal(number, 0),
        _ => new SdpDecimal(ToInteger(value), 0)
    };

    private static long ToInteger(object value) => value switch
    {
        sbyte number => number,
        byte number => number,
        short number => number,
        ushort number => number,
        int number => number,
        uint number => number,
        long number => number,
        ulong number => checked((long)number),
        _ => throw new InvalidCastException("Parameter value must be a signed 64-bit-compatible integer.")
    };

    private static SdpTimestamp ToTimestamp(object value) => value switch
    {
        SdpTimestamp timestamp => timestamp,
        DateTimeOffset timestamp => SdpTimestamp.FromDateTimeOffset(timestamp),
        DateTime timestamp when timestamp.Kind != DateTimeKind.Unspecified =>
            SdpTimestamp.FromDateTimeOffset(new DateTimeOffset(timestamp)),
        _ => throw new InvalidCastException("Timestamp requires an explicit time zone offset.")
    };

    private static DateOnly ToDate(object value) => value switch
    {
#if NETFRAMEWORK
        DateTime date => date.Date,
#else
        DateOnly date => date,
        DateTime date => DateOnly.FromDateTime(date),
#endif
        _ => throw new InvalidCastException("Date requires a DateOnly or DateTime value.")
    };

    private static SdpTime ToTime(object value) => value switch
    {
        SdpTime time => time,
#if !NETFRAMEWORK
        TimeOnly time => SdpTime.FromTimeOnly(time),
#endif
        TimeSpan time when time >= TimeSpan.Zero && time < TimeSpan.FromDays(1) =>
            new SdpTime(time.Hours, time.Minutes, time.Seconds, (int)(time.Ticks % TimeSpan.TicksPerSecond) * 100),
        _ => throw new InvalidCastException("Time requires an SdpTime, TimeOnly or TimeSpan value.")
    };

    private static object EncodeDecimal(SdpDecimal number) =>
        new { t = "decimal", unscaled = number.Unscaled.ToString(Invariant), scale = number.Scale };

    private static object EncodeTimestamp(SdpTimestamp timestamp)
    {
        var seconds = timestamp.Seconds.ToString(Invariant);
        return timestamp.Zone is null
            ? new { t = "timestamp", seconds, nanos = timestamp.Nanos, offset_minutes = timestamp.OffsetMinutes }
            : new { t = "timestamp", seconds, nanos = timestamp.Nanos, offset_minutes = timestamp.OffsetMinutes, zone = timestamp.Zone };
    }

    private static string EncodeBinary(object value)
    {
        ReadOnlySpan<byte> bytes = value switch
        {
            byte[] data => data,
            Memory<byte> data => data.Span,
            ReadOnlyMemory<byte> data => data.Span,
            ArraySegment<byte> data => data.AsSpan(),
            _ => throw new InvalidCastException("Binary requires a byte buffer.")
        };
        return SdpCompatibility.ToHex(bytes);
    }

    private static object EncodeText(string tag, string value)
    {
        ValidateText(value);
        return new { t = tag, v = value };
    }

    private static bool Matches(string? actual, string expected) => actual is null || actual == expected;

    private static string Text(JsonElement wire, string key) =>
        wire.GetProperty(key).GetString() ?? throw new JsonException("SQL wire string is missing.");

    private static long ReadInteger(JsonElement wire, string key)
    {
        var text = Text(wire, key);
        if (text.Length is < 1 or > 20)
            throw new JsonException("SQL wire integer is outside its supported size.");
        var value = long.Parse(text, NumberStyles.AllowLeadingSign, Invariant);
        if (value.ToString(Invariant) != text)
            throw new JsonException("SQL wire integer is not canonical.");
        return value;
    }

    private static SdpDecimal ReadDecimal(JsonElement wire)
    {
        var text = Text(wire, "unscaled");
        if (text.Length is < 1 or > 39)
            throw new JsonException("SQL wire decimal coefficient is outside its supported size.");
        var value = BigInteger.Parse(text, NumberStyles.AllowLeadingSign, Invariant);
        if (value.ToString(Invariant) != text)
            throw new JsonException("SQL wire decimal coefficient is not canonical.");
        return new SdpDecimal(value, wire.GetProperty("scale").GetInt32());
    }

    private static byte[] ReadBinary(JsonElement wire)
    {
        var text = Text(wire, "hex");
        if (text.Any(ch => ch is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new JsonException("SQL wire binary is not canonical.");
        return SdpCompatibility.FromHex(text);
    }

    private static DateOnly ReadDate(JsonElement wire) => DateOnly.ParseExact(Text(wire, "v"), "yyyy-MM-dd", Invariant);
}
