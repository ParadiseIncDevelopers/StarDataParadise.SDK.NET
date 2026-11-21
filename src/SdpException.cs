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

    /// <summary>Original structured server error, including native SQL outcome details.</summary>
    public JsonElement? Details { get; private set; }
    public int? NativeErrno => DetailInt("native_errno");
    public string? Phase => DetailText("phase");
    public string? CommitState => DetailText("commit_state");
    public string? RunState => DetailText("run_state");
    public string? RunId => DetailText("run_id");
    public string? CallId => DetailText("call_id");
    public string? ActionCommitState => DetailText("action_commit_state");
    public int? CauseNativeErrno => DetailInt("cause_native_errno");
    public string? CauseSqlstate => DetailText("cause_sqlstate");
    public JsonElement? Retry => Details is { } json && json.TryGetProperty("retry", out var value) ? value : null;
    private int? DetailInt(string name) => Details is { } json && json.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetInt32() : null;
    private string? DetailText(string name) => Details is { } json && json.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.GetString() : null;

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

        var result = new SdpException(error.GetProperty("code").GetInt32(),
            error.GetProperty("sqlstate").GetString() ?? throw new JsonException("Missing SQLSTATE"),
            error.GetProperty("message").GetString() ?? throw new JsonException("Missing protocol error message"),
            error.GetProperty("unknown_outcome").GetBoolean(), token, OptionalText(error, "correlation_id"),
            error.GetProperty("retryable").GetBoolean(), OptionalInt(error, "line"), OptionalInt(error, "column"));
        result.Details = error.Clone();
        return result;
    }
}
