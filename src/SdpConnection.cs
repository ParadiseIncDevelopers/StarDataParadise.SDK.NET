using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics.CodeAnalysis;

namespace StarDataParadise.SDK;

public sealed class SdpConnection : DbConnection
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SdpWire? _wire;
    private string _connectionString = "";
    private ConnectionOptions? _options;
    private bool _hidePassword;
    private bool _disposed;
    private string? _poolKey;

    internal SdpDataReader? ActiveReader { get; set; }
    internal SdpTransaction? Transaction { get; set; }
    internal SdpWire RequireWire() => _wire is { IsOpen: true } wire ? wire : throw new InvalidOperationException("The connection is closed.");

    public SdpConnection() { }
    public SdpConnection(string connectionString) => ConnectionString = connectionString;

    [AllowNull]
    public override string ConnectionString
    {
        get
        {
            if (!_hidePassword) return _connectionString;
            var settings = new DbConnectionStringBuilder { ConnectionString = _connectionString };
            settings.Remove("Password");
            return settings.ConnectionString;
        }
        set
        {
            if (State != ConnectionState.Closed || _wire is not null)
                throw new InvalidOperationException("Close the connection before changing its configuration.");
            _connectionString = value ?? "";
            _options = null;
            _hidePassword = false;
        }
    }

    public override ConnectionState State => _wire?.IsOpen == true ? ConnectionState.Open : ConnectionState.Closed;
    public override string Database => "main";
    public override string DataSource => _options?.Host ?? "";
    public override string ServerVersion => RequireWire().ProductVersion;
    public override int ConnectionTimeout => _options?.TimeoutSeconds ?? 15;
    public string SessionId => RequireWire().SessionId;

    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task OpenAsync(CancellationToken cancellationToken) => LockAsync(async () =>
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SdpConnection));
        if (State == ConnectionState.Open) throw new InvalidOperationException("The connection is already open.");
        _wire?.Dispose();
        _wire = null;
        var options = _options = ConnectionOptions.Parse(_connectionString);
        _poolKey = options.Pooling ? options.Key() : null;
        var pooled = _poolKey is null ? null : WirePool.Take(_poolKey);
        if (pooled is not null)
        {
            try
            {
                await pooled.CallAsync("session_reset", null, cancellationToken).ConfigureAwait(false);
                _wire = pooled;
            }
            catch (SdpException)
            {
                pooled.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        _wire ??= await options.OpenAsync(cancellationToken).ConfigureAwait(false);
        _hidePassword = true;
        return true;
    }, cancellationToken);

    public override void Close() => CloseAsync().GetAwaiter().GetResult();

    public
#if !NETFRAMEWORK
        override
#endif
        Task CloseAsync() => LockAsync(async () =>
    {
        var wire = _wire;
        if (wire is null) return true;
        try
        {
            if (ActiveReader is { } reader)
                await reader.FinishAsync(commitRoutine: false, CancellationToken.None).ConfigureAwait(false);
            if (_poolKey is not null && wire.IsOpen)
            {
                await wire.CallAsync("session_reset", null, CancellationToken.None).ConfigureAwait(false);
                WirePool.Return(_poolKey, wire);
                wire = null;
            }
        }
        catch (SdpException) { }
        finally
        {
            wire?.Dispose();
            _wire = null;
            ActiveReader = null;
            Transaction?.Detach();
            Transaction = null;
        }
        return true;
    }, CancellationToken.None);

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            Close();
        }
        base.Dispose(disposing);
    }

    public
#if !NETFRAMEWORK
        override
#endif
        async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await CloseAsync().ConfigureAwait(false);
        }
        GC.SuppressFinalize(this);
    }

    public override void ChangeDatabase(string databaseName) =>
        throw new NotSupportedException("The SQL endpoint has a fixed current database.");

    protected override DbCommand CreateDbCommand() => new SdpCommand { Connection = this };

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        BeginDbTransactionAsync(isolationLevel, CancellationToken.None).AsTask().GetAwaiter().GetResult();

#if NETFRAMEWORK
    private
#else
    protected override
#endif
        async ValueTask<DbTransaction> BeginDbTransactionAsync(IsolationLevel isolationLevel,
        CancellationToken cancellationToken) => await LockAsync(async () =>
    {
        if (isolationLevel is not (IsolationLevel.Unspecified or IsolationLevel.Serializable))
            throw new NotSupportedException("The Server provides serializable transactions.");
        RequireNoResult();
        if (Transaction is not null) throw new InvalidOperationException("A transaction is already active.");
        await RequireWire().CallAsync("tx_begin", null, cancellationToken).ConfigureAwait(false);
        return Transaction = new SdpTransaction(this);
    }, cancellationToken).ConfigureAwait(false);

    internal async Task<T> LockAsync<T>(Func<Task<T>> body, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await body().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal void RequireNoResult()
    {
        if (ActiveReader is not null) throw new InvalidOperationException("Close the active reader before using the connection.");
    }

    internal async Task<bool> CancelAsync(SdpWire target, uint requestId)
    {
        var options = _options;
        if (options is null || requestId == 0 || !target.IsOpen) return false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var control = await options.OpenAsync(timeout.Token).ConfigureAwait(false);
        if (target.CurrentRequestId != requestId) return false;
        var response = await control.CallAsync("query_cancel", new { session_id = target.SessionId, request_id = requestId },
            timeout.Token).ConfigureAwait(false);
        return response.GetProperty("accepted").GetBoolean();
    }

    internal static string NewToken() => Guid.NewGuid().ToString("N");

    private Task<JsonElement> ControlAsync(string operation, object? body, CancellationToken token, string? requestToken = null) =>
        LockAsync(() =>
        {
            RequireNoResult();
            return RequireWire().CallAsync(operation, body, token, requestToken);
        }, token);

    public Task<JsonElement> GetCapabilitiesAsync(CancellationToken token = default) => ControlAsync("capabilities", null, token);
    public Task<JsonElement> GetHealthAsync(CancellationToken token = default) => ControlAsync("health", null, token);
    public Task<JsonElement> GetMetadataAsync(CancellationToken token = default) => ControlAsync("metadata", null, token);
    public Task<JsonElement> GetOutcomeAsync(string requestToken, CancellationToken token = default) =>
        ControlAsync("tx_outcome", new { request_token = ValidateToken(requestToken) }, token);

    public Task<JsonElement> GetHistoryAsync(string target, int limit = 128, CancellationToken token = default) =>
        ControlAsync("history_log", new { target, limit }, token);

    public Task<JsonElement> ReadHistoryAsync(string target, string revision, CancellationToken token = default) =>
        ControlAsync("history_read", new { target, revision }, token);

    public Task<JsonElement> PredictAsync(string name, IReadOnlyDictionary<string, object?> features, CancellationToken token = default) =>
        ControlAsync("model_predict", new { name, features }, token);

    public Task<JsonElement> GetModelAsync(string name, CancellationToken token = default) =>
        ControlAsync("model_status", new { name }, token);

    public Task<JsonElement> GetDatasetAsync(string datasetId, CancellationToken token = default) =>
        ControlAsync("dataset_get", new { dataset_id = datasetId }, token);

    public Task<JsonElement> GetJobAsync(string jobId, CancellationToken token = default) =>
        ControlAsync("job_status", new { job_id = jobId }, token);

    public Task<JsonElement> CancelJobAsync(string jobId, CancellationToken token = default) =>
        ControlAsync("job_cancel", new { job_id = jobId }, token);

    public Task<JsonElement> SubmitComputeAsync(int count, CancellationToken token = default)
    {
        var key = NewToken();
        return ControlAsync("job_submit", new { request_token = key, kind = "compute", count }, token, key);
    }

    public Task<JsonElement> ValidateDecisionAsync(string sql, CancellationToken token = default) =>
        ControlAsync("decision_validate", new { sql }, token);

    public Task<JsonElement> GetAdminStatusAsync(CancellationToken token = default) =>
        ControlAsync("admin_status", null, token);

    public Task<JsonElement> GetAdminHealthAsync(CancellationToken token = default) =>
        ControlAsync("admin_health", null, token);

    internal static string ValidateToken(string token)
    {
        if (token is null) throw new ArgumentNullException(nameof(token));
        if (token.Length != 32 || token.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Use a 32-character lowercase hexadecimal request token.", nameof(token));
        return token;
    }

    internal static async Task<(SdpWire Wire, int TimeoutSeconds)> OpenDedicatedNativeWireAsync(string connectionString, CancellationToken token)
    {
        var settings = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (!settings.ContainsKey("Timeout")) settings["Timeout"] = 75;
        var options = ConnectionOptions.Parse(settings.ConnectionString);
        if (options.Pooling) throw new ArgumentException("Native SQL sessions use dedicated connections; disable Pooling.");
        return (await options.OpenAsync(token).ConfigureAwait(false), options.TimeoutSeconds);
    }

    private sealed record ConnectionOptions(string Host, int Port, string TlsName, string? CaFile, string User,
        string Password, int TimeoutSeconds, bool Pooling, IPAddress? PlaintextAddress)
    {
        internal static ConnectionOptions Parse(string value)
        {
            var settings = new DbConnectionStringBuilder { ConnectionString = value };
            foreach (string key in settings.Keys)
            {
                if (key.ToUpperInvariant() is not ("HOST" or "PORT" or "TLS NAME" or "TLS MODE" or "CA FILE"
                    or "USER" or "PASSWORD" or "TIMEOUT" or "POOLING"))
                    throw new ArgumentException("Unsupported StarDataParadise connection option.");
            }
            static string Read(DbConnectionStringBuilder settings, string key, string fallback = "") =>
                settings.TryGetValue(key, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "" : fallback;
            var user = Read(settings, "User");
            var password = Read(settings, "Password");
            if (user.Length == 0 || password.Length == 0) throw new ArgumentException("User and Password are required.");
            var host = Read(settings, "Host", "127.0.0.1");
            if (host.Length == 0) throw new ArgumentException("Host cannot be empty.");
            if (!int.TryParse(Read(settings, "Port", "7443"), out var port) || port is < 1 or > 65535)
                throw new ArgumentException("Port must be between 1 and 65535.");
            if (!int.TryParse(Read(settings, "Timeout", "15"), out var timeout) || timeout is < 1 or > 300)
                throw new ArgumentException("Timeout must be between 1 and 300 seconds.");
            if (!bool.TryParse(Read(settings, "Pooling", "false"), out var pooling))
                throw new ArgumentException("Pooling must be true or false.");
            var mode = Read(settings, "TLS Mode", "Required");
            IPAddress? plaintextAddress = null;
            if (mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
            {
                if (settings.ContainsKey("CA File") || settings.ContainsKey("TLS Name"))
                    throw new ArgumentException("CA File and TLS Name require TLS Mode=Required.");

                if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    plaintextAddress = IPAddress.Loopback;
                else if (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address))
                    plaintextAddress = address;
                else
                    throw new ArgumentException("TLS Mode=Disabled requires localhost or a loopback IP address.");
            }
            else if (!mode.Equals("Required", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("TLS Mode must be Required or Disabled.");
            }

            var ca = Read(settings, "CA File");
            return new(host, port, Read(settings, "TLS Name", host), ca.Length > 0 ? Path.GetFullPath(ca) : null,
                user, password, timeout, pooling, plaintextAddress);
        }

        internal Task<SdpWire> OpenAsync(CancellationToken cancellationToken) =>
            SdpWire.OpenAsync(Host, Port, TlsName, CaFile, PlaintextAddress, User, Password,
                TimeSpan.FromSeconds(TimeoutSeconds), cancellationToken);

        internal string Key()
        {
            var identity = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Host, Port, TlsName, User, Password, TimeoutSeconds,
                PlaintextAddress = PlaintextAddress?.ToString(),
                Certificate = CaFile is null ? "" : SdpCompatibility.Sha256Hex(File.ReadAllBytes(CaFile)),
            });
            try { return SdpCompatibility.Sha256Hex(identity); }
            finally { Array.Clear(identity, 0, identity.Length); }
        }
    }

    private static class WirePool
    {
        private static readonly object Gate = new();
        private static readonly List<Entry> Available = [];
        private sealed record Entry(string Key, SdpWire Wire, DateTimeOffset ReturnedAt);

        internal static SdpWire? Take(string key)
        {
            lock (Gate)
            {
                var old = DateTimeOffset.UtcNow.AddSeconds(-10);
                for (var i = Available.Count - 1; i >= 0; i--)
                {
                    var entry = Available[i];
                    if (entry.ReturnedAt < old || !entry.Wire.IsOpen)
                    {
                        Available.RemoveAt(i);
                        entry.Wire.Dispose();
                    }
                    else if (entry.Key == key)
                    {
                        Available.RemoveAt(i);
                        return entry.Wire;
                    }
                }
                return null;
            }
        }

        internal static void Return(string key, SdpWire wire)
        {
            lock (Gate)
            {
                if (Available.Count >= 8)
                {
                    Available[0].Wire.Dispose();
                    Available.RemoveAt(0);
                }
                Available.Add(new Entry(key, wire, DateTimeOffset.UtcNow));
            }
        }
    }
}
