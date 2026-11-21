using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StarDataParadise.SDK;

/// <summary>Exact serialized JSON; integer digits are never converted to CLR floating point.</summary>
public sealed class SdpNativeJson
{
    public SdpNativeJson(string text)
    {
        if (text is null) throw new ArgumentNullException(nameof(text));
        SdpValues.ValidateText(text);
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
        Text = text;
    }
    public string Text { get; }
    public override string ToString() => Text;
}

/// <summary>A native MySQL DECIMAL with up to 65 coefficient digits and scale 30.</summary>
public sealed class SdpNativeDecimal
{
    public SdpNativeDecimal(string text)
    {
        if (text is null || !Regex.IsMatch(text, @"\A-?(?:0|[1-9][0-9]{0,64})(?:\.[0-9]{1,30})?\z"))
            throw new ArgumentException("Invalid native DECIMAL.", nameof(text));
        var parts = text.TrimStart('-').Split('.');
        if (string.Concat(parts).TrimStart('0').Length > 65) throw new ArgumentOutOfRangeException(nameof(text));
        Text = text;
        Scale = parts.Length == 2 ? parts[1].Length : 0;
        Unscaled = BigInteger.Parse(text.Replace(".", ""), CultureInfo.InvariantCulture);
    }
    public string Text { get; }
    public int Scale { get; }
    public BigInteger Unscaled { get; }
    public override string ToString() => Text;
}

/// <summary>Native temporal text. MySQL TIME is a signed interval and may exceed 24 hours.</summary>
public sealed class SdpNativeTemporal
{
    public SdpNativeTemporal(string sqlType, string text)
    {
        if (sqlType is not ("DATE" or "TIME" or "DATETIME" or "TIMESTAMP")) throw new ArgumentException("Invalid native temporal type.", nameof(sqlType));
        if (text is null || text.Length > 64) throw new ArgumentException("Invalid native temporal value.", nameof(text));
        SdpValues.ValidateText(text); SqlType = sqlType; Text = text;
    }
    public string SqlType { get; }
    public string Text { get; }
    public override string ToString() => Text;
}

public sealed record SdpNativeColumn(string Name, string SqlType, int NativeType, bool Nullable);
public sealed record SdpNativeResultSet(IReadOnlyList<SdpNativeColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows, BigInteger AffectedRows, BigInteger LastInsertId);
public sealed record SdpNativeResult(IReadOnlyList<SdpNativeResultSet> Sets, bool InTransaction, bool Autocommit);

/// <summary>A dedicated native SQL session. It does not use the legacy ADO.NET CRUD receipt API.</summary>
public sealed class SdpNativeConnection : IDisposable
{
    private readonly SdpWire _wire;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly int _timeoutMs;
    private bool _disposed;
    private SdpNativeConnection(SdpWire wire, int timeoutMs, bool autocommit) =>
        (_wire, _timeoutMs, Autocommit) = (wire, timeoutMs, autocommit);
    public string SessionId => _wire.SessionId;
    public bool IsOpen => !_disposed && _wire.IsOpen;
    /// <summary>Transaction state from the last successful native response.</summary>
    public bool InTransaction { get; private set; }
    public bool Autocommit { get; private set; }

    public static async Task<SdpNativeConnection> OpenAsync(string connectionString, bool autocommit = true,
        CancellationToken cancellationToken = default)
    {
        var opened = await SdpConnection.OpenDedicatedNativeWireAsync(connectionString, cancellationToken).ConfigureAwait(false);
        var wire = opened.Wire;
        try
        {
            var capabilities = await wire.CallAsync("capabilities", null, cancellationToken).ConfigureAwait(false);
            if (!capabilities.GetProperty("features").TryGetProperty("native_sql_v1", out var feature) || feature.ValueKind != JsonValueKind.True)
                throw new SdpException(1004, "0A000", "Server does not advertise native_sql_v1.");
            await wire.CallAsync("native.open", new { autocommit }, cancellationToken).ConfigureAwait(false);
            return new SdpNativeConnection(wire, Math.Max(1000, Math.Min(300000, opened.TimeoutSeconds * 1000 - 2000)), autocommit);
        }
        catch { wire.Dispose(); throw; }
    }

    public async Task<SdpNativeResult> ExecuteAsync(string sql, IReadOnlyList<object?>? parameters = null,
        int maxRows = 1000, CancellationToken cancellationToken = default)
    {
        if (sql is null || sql.Length == 0 || sql.IndexOf('\0') >= 0 || SdpValues.ValidateText(sql) > 65536)
            throw new ArgumentException("Native SQL requires 1 to 65536 UTF-8 bytes without NUL.", nameof(sql));
        if (maxRows is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxRows));
        if (parameters?.Count > 128) throw new ArgumentOutOfRangeException(nameof(parameters));
        var values = parameters?.Select(Encode).ToArray() ?? Array.Empty<object>();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen) throw new ObjectDisposedException(nameof(SdpNativeConnection));
            var body = await _wire.CallAsync("native.execute", new { sql, parameters = values, max_rows = maxRows, timeout_ms = _timeoutMs },
                cancellationToken, uncertainOnSend: true).ConfigureAwait(false);
            try
            {
                body = await ReassembleAsync(body, cancellationToken).ConfigureAwait(false);
                var result = Decode(body, maxRows);
                (InTransaction, Autocommit) = (result.InTransaction, result.Autocommit);
                return result;
            }
            catch (SdpException) { throw; }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException or KeyNotFoundException or OverflowException)
            {
                Dispose();
                throw new SdpException(1001, "08P01", "Malformed native result; query the operation outcome before retrying.", true, inner: error);
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<JsonElement> ReassembleAsync(JsonElement response, CancellationToken token)
    {
        if (!response.TryGetProperty("native_result", out var first)) return response;
        var id = SdpConnection.ValidateToken(Text(first, "id"));
        var total = first.GetProperty("total_bytes").GetInt32();
        var hash = Text(first, "sha256");
        if (total is < 1 or > 16 * 1024 * 1024 || !Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z")) throw new JsonException("Invalid native result budget.");
        var payload = new byte[total]; var offset = 0;
        for (;;)
        {
            var part = response.GetProperty("native_result");
            if (Text(part, "id") != id || part.GetProperty("offset").GetInt32() != offset ||
                part.GetProperty("total_bytes").GetInt32() != total || Text(part, "sha256") != hash) throw new JsonException("Native result chunks differ.");
            var encoded = Text(part, "chunk"); var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length is < 1 or > 256 * 1024 || Convert.ToBase64String(bytes) != encoded || bytes.Length > total - offset)
                throw new JsonException("Invalid native result chunk.");
            Buffer.BlockCopy(bytes, 0, payload, offset, bytes.Length); offset += bytes.Length;
            if (part.GetProperty("end").GetBoolean()) break;
            if (offset == total) throw new JsonException("Native result is missing its final marker.");
            response = await _wire.CallAsync("native.fetch", new { result_id = id, offset }, token, uncertainOnSend: true).ConfigureAwait(false);
        }
        if (offset != total || SdpCompatibility.Sha256Hex(payload) != hash) throw new JsonException("Native result checksum differs.");
        using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(document.RootElement);
        return document.RootElement.Clone();
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate native result field."); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
    }

    private static SdpNativeResult Decode(JsonElement body, int maxRows)
    {
        var sets = new List<SdpNativeResultSet>();
        var rowCount = 0;
        foreach (var set in body.GetProperty("sets").EnumerateArray())
        {
            if (sets.Count == 32) throw new JsonException("Too many native result sets.");
            var columns = set.GetProperty("columns").EnumerateArray().Select(c => new SdpNativeColumn(Text(c, "name"), Text(c, "type"), c.GetProperty("native_type").GetInt32(), c.GetProperty("nullable").GetBoolean())).ToArray();
            if (columns.Length > 128) throw new JsonException("Too many native result columns.");
            var rows = new List<IReadOnlyList<object?>>();
            foreach (var row in set.GetProperty("rows").EnumerateArray())
            {
                if (++rowCount > maxRows) throw new JsonException("Native result exceeds the requested row budget.");
                if (row.GetArrayLength() != columns.Length) throw new JsonException("Native row shape differs.");
                rows.Add(Array.AsReadOnly(row.EnumerateArray().Select((value, index) => DecodeValue(value, columns[index])).ToArray()));
            }
            var affected = Count(Text(set, "affected_rows"));
            var inserted = set.TryGetProperty("last_insert_id", out var v) ? Count(v.GetString()!) : BigInteger.Zero;
            sets.Add(new SdpNativeResultSet(Array.AsReadOnly(columns), rows.AsReadOnly(), affected, inserted));
        }
        return new SdpNativeResult(sets.AsReadOnly(), body.GetProperty("in_transaction").GetBoolean(), body.GetProperty("autocommit").GetBoolean());
    }

    private static object? DecodeValue(JsonElement value, SdpNativeColumn column) => Text(value, "t") switch
    {
        "null" => null,
        "i64" => long.Parse(Text(value, "v"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
        "u64" => ulong.Parse(Text(value, "v"), NumberStyles.None, CultureInfo.InvariantCulture),
        "decimal" => new SdpNativeDecimal(Text(value, "v")),
        "float" => Finite(double.Parse(Text(value, "v"), NumberStyles.Float, CultureInfo.InvariantCulture)),
        "binary" => DecodeBinary(Text(value, "v")),
        "json" => new SdpNativeJson(Text(value, "v")),
        "date" or "datetime" or "time" => new SdpNativeTemporal(column.SqlType, Text(value, "v")),
        "text" => Text(value, "v"),
        _ => throw new JsonException("Unsupported native SQL value tag.")
    };

    public static object Encode(object? value)
    {
        object Tag(string tag, string text) { SdpValues.ValidateText(text); return new { t = tag, v = text }; }
        if (value is null or DBNull) return new { t = "null" };
        if (value is SdpNativeJson json) return Tag("json", json.Text);
        if (value is SdpNativeDecimal number) return Tag("decimal", number.Text);
        if (value is SdpDecimal oldDecimal) return Tag("decimal", oldDecimal.ToString());
        if (value is decimal exact) return Tag("decimal", exact.ToString(CultureInfo.InvariantCulture));
        if (value is SdpNativeTemporal temporal) return Tag("text", temporal.Text);
        if (value is string text) return Tag("text", text);
        if (value is bool boolean) return new { t = "bool", v = boolean };
        if (value is byte[] bytes) return Tag("binary", SdpCompatibility.ToHex(bytes));
        if (value is ulong unsigned) return Tag(unsigned <= long.MaxValue ? "i64" : "u64", unsigned.ToString(CultureInfo.InvariantCulture));
        if (value is BigInteger big)
        {
            if (big < long.MinValue || big > ulong.MaxValue) throw new ArgumentOutOfRangeException(nameof(value));
            return Tag(big <= long.MaxValue ? "i64" : "u64", big.ToString(CultureInfo.InvariantCulture));
        }
        if (value is sbyte or byte or short or ushort or int or uint or long) return Tag("i64", Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
        if (value is double or float) return Tag("float", Finite(Convert.ToDouble(value, CultureInfo.InvariantCulture)).ToString("R", CultureInfo.InvariantCulture));
        throw new ArgumentException("Unsupported native SQL binding.", nameof(value));
    }
    private static double Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? throw new FormatException("Native floating point must be finite.") : value;
    private static byte[] DecodeBinary(string value) => Regex.IsMatch(value, @"\A(?:[0-9a-f]{2})*\z") ? SdpCompatibility.FromHex(value) : throw new JsonException("Invalid native binary encoding.");
    private static BigInteger Count(string value) => Regex.IsMatch(value, @"\A(?:0|[1-9][0-9]{0,19})\z") ? new BigInteger(ulong.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture)) : throw new JsonException("Invalid native affected count.");
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new JsonException("Missing native result text.");

    public Task<SdpNativeResult> BeginAsync(CancellationToken token = default) => ExecuteAsync("START TRANSACTION", cancellationToken: token);
    public Task<SdpNativeResult> RollbackAsync(CancellationToken token = default) => ExecuteAsync("ROLLBACK", cancellationToken: token);
    public Task<SdpNativeResult> CommitAsync(string? requestKey = null, int? waitTimeoutMs = null, bool asynchronous = false, CancellationToken token = default)
    {
        if (asynchronous && waitTimeoutMs is not null) throw new ArgumentException("COMMIT ASYNC cannot wait for a response.");
        var options = new List<string>(); var values = new List<object?>();
        if (requestKey is not null) { options.Add("request_key=?"); values.Add(SdpConnection.ValidateToken(requestKey)); }
        if (waitTimeoutMs is not null)
        { if (waitTimeoutMs < 1 || waitTimeoutMs > _timeoutMs - 1000) throw new ArgumentOutOfRangeException(nameof(waitTimeoutMs)); options.Add("wait_timeout_ms=?"); values.Add(waitTimeoutMs.Value); }
        return ExecuteAsync((asynchronous ? "COMMIT ASYNC" : "COMMIT") + (options.Count > 0 ? " WITH (" + string.Join(", ", options) + ")" : ""), values, cancellationToken: token);
    }
    public Task<SdpNativeResult> FetchResponseAsync(string runId, CancellationToken token = default) => ExecuteAsync("FETCH RESPONSE FROM RUN ?", new object?[] { SdpConnection.ValidateToken(runId) }, cancellationToken: token);
    public Task<SdpNativeResult> CancelRunAsync(string runId, CancellationToken token = default) => ExecuteAsync("CANCEL RUN ?", new object?[] { SdpConnection.ValidateToken(runId) }, cancellationToken: token);
    public void Dispose() { if (!_disposed) { _disposed = true; _wire.Dispose(); } }
    public ValueTask DisposeAsync() { Dispose(); return default; }
}
