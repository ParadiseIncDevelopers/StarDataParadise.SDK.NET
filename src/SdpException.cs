using System.Data.Common;
using System.Text.Json;

namespace StarDataParadise.SDK;

public sealed class SdpException : DbException
{
    internal SdpException(int code, string sqlState, string message, bool unknownOutcome = false,
        string? requestToken = null, string? correlationId = null, bool retryable = false,
        int? line = null, int? column = null, Exception? inner = null) : base(message, inner)
    {
        Code = code;
        SqlState = sqlState;
        UnknownOutcome = unknownOutcome;
        RequestToken = requestToken;
        CorrelationId = correlationId;
        Retryable = retryable;
        Line = line;
        Column = column;
        HResult = code;
    }

    public int Code { get; }
#if NETFRAMEWORK
    public string SqlState { get; }
#else
    public override string SqlState { get; }
#endif
    public bool UnknownOutcome { get; }
    public string? RequestToken { get; }
    public string? CorrelationId { get; }
    public bool Retryable { get; }
#if NETFRAMEWORK
    public bool IsTransient => Retryable && !UnknownOutcome;
#else
    public override bool IsTransient => Retryable && !UnknownOutcome;
#endif
    public int? Line { get; }
    public int? Column { get; }

    internal static SdpException FromWire(JsonElement error, string? token)
    {
        static string? OptionalText(JsonElement json, string key)
        {
            return json.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
                ? value.GetString() : null;
        }

        static int? OptionalInt(JsonElement json, string key)
        {
            return json.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
                ? value.GetInt32() : null;
        }

        return new SdpException(error.GetProperty("code").GetInt32(),
            error.GetProperty("sqlstate").GetString() ?? throw new JsonException("Missing SQLSTATE"),
            error.GetProperty("message").GetString() ?? throw new JsonException("Missing protocol error message"),
            error.GetProperty("unknown_outcome").GetBoolean(), token, OptionalText(error, "correlation_id"),
            error.GetProperty("retryable").GetBoolean(), OptionalInt(error, "line"), OptionalInt(error, "column"));
    }
}
