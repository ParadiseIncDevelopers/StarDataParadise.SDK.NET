using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace StarDataParadise.SDK;

public sealed class SdpParameter : DbParameter
{
    private DbType? _dbType;
    private byte? _scale;
    private string _name = "";
    private string _source = "";
    private ParameterDirection _direction = ParameterDirection.Input;

    public SdpParameter() {}
    public SdpParameter(string name, object? value) => (ParameterName, Value) = (name, value);

    public SdpParameter(string name, DbType type) => (ParameterName, DbType) = (name, type);

    public override DbType DbType
    {
        get => _dbType ?? InferredDbType(SqlTypeName ?? SdpValues.InferType(Value));
        set
        {
            _ = TypeFor(value);
            _dbType = value;
        }
    }

    /// <summary>Optional explicit SQL type name, for example RECORD or COLLECTION with a NULL value.</summary>
    public string? SqlTypeName { get; set; }

    public override ParameterDirection Direction
    {
        get => _direction;
        set => _direction = value switch
        {
            ParameterDirection.Input or ParameterDirection.Output or ParameterDirection.InputOutput or
                ParameterDirection.ReturnValue => value,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
    }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName
    {
        get => _name;
        set => _name = value ?? "";
    }

    [AllowNull]
    public override string SourceColumn
    {
        get => _source;
        set => _source = value ?? "";
    }

    public override object? Value { get; set; }
    public override int Size { get; set; }
    public override byte Precision { get; set; }
    public override byte Scale
    {
        get => _scale ?? 0;
        set => _scale = value;
    }
    public override bool SourceColumnNullMapping { get; set; }
    public override DataRowVersion SourceVersion { get; set; } = DataRowVersion.Current;

    public override void ResetDbType()
    {
        _dbType = null;
        SqlTypeName = null;
    }

    internal string WireName => CanonicalName(ParameterName, true);

    internal (object Declaration, object Value) Bind()
    {
        var declared = SqlTypeName is null ? null : SdpValues.BaseType(SqlTypeName.Trim().ToUpperInvariant());
        var type = declared ?? (_dbType is { } dbType ? TypeFor(dbType) : null) ?? SdpValues.InferType(Value);
        if (type == "NULL")
            throw new NotSupportedException("Specify the SQL type of a NULL parameter.");
        _ = SdpValues.ClrType(type);

        if (type != "DECIMAL") return (new { type }, SdpValues.Encode(type, Value));

        var number = Value is null or DBNull ? (SdpDecimal?)null : SdpValues.ToFixed(Value);
        var scale = DecimalShape(SqlTypeName)?.Scale ?? (_scale.HasValue ? _scale.Value : number?.Scale ?? 0);
        if (scale > 38)
            throw new ArgumentOutOfRangeException(nameof(Scale), "A SQL decimal scale is at most 38.");
        if (number is { } original && original.Scale != scale)
            number = Rescale(original, scale);
        var digits = number is { } exact ? BigInteger.Abs(exact.Unscaled).ToString(CultureInfo.InvariantCulture).Length : 1;
        var precision = DecimalShape(SqlTypeName)?.Precision ?? (Precision == 0 ? (number.HasValue ? Math.Max(scale, digits) : 38) : Precision);
        if (precision is < 1 or > 38 || scale > precision || digits > precision)
            throw new ArgumentOutOfRangeException(nameof(Precision), "Value and scale must fit the declared DECIMAL precision.");

        return (new { type, precision, scale }, SdpValues.Encode(type, number));
    }

    internal static string CanonicalName(string name, bool required = false)
    {
        if (name.Length > 0 && name[0] is ':' or '@')
            name = name.Substring(1);
        if (name.Length >= 2 && name[0] == '"' && name[name.Length - 1] == '"')
            name = name.Substring(1, name.Length - 2).Replace("\"\"", "\"");
        else
            name = name.ToLowerInvariant();
        if (name.Contains('\0') || SdpValues.ValidateText(name) > 128 || (required && name.Length == 0))
            throw new ArgumentException("Parameter name must contain 1 to 128 UTF-8 bytes.", nameof(name));
        return name;
    }

    private static (int Precision, int Scale)? DecimalShape(string? name)
    {
        if (name is null) return null;
        name = name.Trim().ToUpperInvariant();
        if (!name.StartsWith("DECIMAL(", StringComparison.Ordinal)) return null;
        if (!name.EndsWith(")", StringComparison.Ordinal))
            throw new ArgumentException("Invalid DECIMAL precision and scale.", nameof(SqlTypeName));
        var dims = name.Substring(8, name.Length - 9).Split(',').Select(value => value.Trim()).ToArray();
        if (dims.Length != 2 || !int.TryParse(dims[0], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ||
            !int.TryParse(dims[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s))
            throw new ArgumentException("Invalid DECIMAL precision and scale.", nameof(SqlTypeName));
        return (p, s);
    }

    private static SdpDecimal Rescale(SdpDecimal number, int scale)
    {
        if (scale > number.Scale)
            return new SdpDecimal(number.Unscaled * BigInteger.Pow(10, scale - number.Scale), scale);
        var scaled = BigInteger.DivRem(number.Unscaled, BigInteger.Pow(10, number.Scale - scale), out var remainder);
        if (!remainder.IsZero)
            throw new InvalidCastException("SQL decimal scale would discard fractional digits.");
        return new SdpDecimal(scaled, scale);
    }

    private static string? TypeFor(DbType type) => type switch
    {
        DbType.Boolean => "BOOLEAN",
        DbType.SByte or DbType.Byte or DbType.Int16 or DbType.UInt16 or DbType.Int32 or DbType.UInt32 or
            DbType.Int64 or DbType.UInt64 => "BIGINT",
        DbType.Decimal or DbType.VarNumeric or DbType.Currency => "DECIMAL",
        DbType.String or DbType.StringFixedLength or DbType.AnsiString or DbType.AnsiStringFixedLength => "TEXT",
        DbType.Binary => "BLOB",
        DbType.Date => "DATE",
        DbType.Time => "TIME",
        DbType.DateTimeOffset or DbType.DateTime or DbType.DateTime2 => "TIMESTAMP WITH TIME ZONE",
        DbType.Object => null,
        _ => throw new NotSupportedException("The requested DbType has no exact SQL wire representation.")
    };

    private static DbType InferredDbType(string type) => SdpValues.BaseType(type.Trim().ToUpperInvariant()) switch
    {
        "BOOLEAN" => DbType.Boolean,
        "BIGINT" => DbType.Int64,
        "DECIMAL" => DbType.Decimal,
        "TEXT" => DbType.String,
        "BLOB" => DbType.Binary,
        "DATE" => DbType.Date,
        "TIME" => DbType.Time,
        "TIMESTAMP WITH TIME ZONE" => DbType.DateTimeOffset,
        _ => DbType.Object
    };
}

public sealed class SdpParameterCollection : DbParameterCollection
{
    private const int MaxParameters = 64;
    private readonly List<SdpParameter> _items = new();
    internal IReadOnlyList<SdpParameter> Items => _items;

    public override int Count => _items.Count;
    public override object SyncRoot => ((ICollection)_items).SyncRoot;
    public override bool IsFixedSize => false;
    public override bool IsReadOnly => false;
    public override bool IsSynchronized => false;

    public SdpParameter Add(string name, DbType type)
    {
        var parameter = new SdpParameter(name, type);
        Add(parameter);
        return parameter;
    }

    public SdpParameter AddWithValue(string name, object? value)
    {
        var parameter = new SdpParameter(name, value);
        Add(parameter);
        return parameter;
    }

    public override int Add(object value)
    {
        var index = Count;
        Insert(index, value);
        return index;
    }

    public override void AddRange(Array values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        if (values.Length > MaxParameters - Count)
            throw new ArgumentException("A command supports up to 64 parameters.", nameof(values));

        var staged = new List<SdpParameter>(values.Length);
        foreach (var value in values)
        {
            var parameter = RequireParameter(value);
            ValidateNew(parameter, -1);
            if (staged.Any(other => Conflicts(other, parameter)))
                throw new ArgumentException("Parameter instance or name is already in the collection.", nameof(values));
            staged.Add(parameter);
        }
        _items.AddRange(staged);
    }

    public override void Clear() => _items.Clear();
    public override bool Contains(object value) => IndexOf(value) >= 0;
    public override bool Contains(string value) => IndexOf(value) >= 0;
    public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
    public override IEnumerator GetEnumerator() => _items.GetEnumerator();
    public override int IndexOf(object value) => value is SdpParameter parameter ? _items.IndexOf(parameter) : -1;

    public override int IndexOf(string parameterName)
    {
        if (parameterName is null) throw new ArgumentNullException(nameof(parameterName));
        var name = SdpParameter.CanonicalName(parameterName);
        return _items.FindIndex(parameter => SdpParameter.CanonicalName(parameter.ParameterName) == name);
    }

    public override void Insert(int index, object value)
    {
        if (Count >= MaxParameters)
            throw new ArgumentException("A command supports up to 64 parameters.", nameof(value));
        var parameter = RequireParameter(value);
        ValidateNew(parameter, -1);
        _items.Insert(index, parameter);
    }

    public override void Remove(object value)
    {
        var parameter = RequireParameter(value);
        if (!_items.Remove(parameter))
            throw new ArgumentException("Parameter was not in the collection.", nameof(value));
    }

    public override void RemoveAt(int index) => _items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => _items.RemoveAt(Find(parameterName));
    protected override DbParameter GetParameter(int index) => _items[index];
    protected override DbParameter GetParameter(string parameterName) => _items[Find(parameterName)];

    protected override void SetParameter(int index, DbParameter value)
    {
        var parameter = RequireParameter(value);
        ValidateNew(parameter, index);
        _items[index] = parameter;
    }

    protected override void SetParameter(string parameterName, DbParameter value) => SetParameter(Find(parameterName), value);

    private int Find(string name)
    {
        var index = IndexOf(name);
        return index >= 0 ? index : throw new IndexOutOfRangeException("Parameter was not in the collection.");
    }

    private void ValidateNew(SdpParameter parameter, int except)
    {
        _ = SdpParameter.CanonicalName(parameter.ParameterName);
        for (var i = 0; i < _items.Count; i++)
        {
            if (i != except && Conflicts(_items[i], parameter))
                throw new ArgumentException("Parameter instance or name is already in the collection.", nameof(parameter));
        }
    }

    private static bool Conflicts(SdpParameter first, SdpParameter second)
    {
        if (ReferenceEquals(first, second)) return true;
        var name = SdpParameter.CanonicalName(first.ParameterName);
        return name.Length > 0 && name == SdpParameter.CanonicalName(second.ParameterName);
    }

    private static SdpParameter RequireParameter(object? value) => value as SdpParameter ??
        throw new ArgumentException("This provider requires SdpParameter instances.", nameof(value));
}
