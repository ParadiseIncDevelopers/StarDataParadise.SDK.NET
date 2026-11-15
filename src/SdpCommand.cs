using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StarDataParadise.SDK;

public sealed class SdpCommand : DbCommand
{
    private readonly SdpParameterCollection _parameters = new();
    private SdpConnection? _connection;
    private SdpTransaction? _transaction;
    private string _commandText = "";
    private int _timeout = 30;
    private Running? _running;
    private sealed record Running(SdpConnection Connection, SdpWire Wire, CancellationTokenSource Source);

    [AllowNull]
    public override string CommandText { get => _commandText; set => _commandText = value ?? ""; }
    public override int CommandTimeout
    {
        get => _timeout;
        set => _timeout = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }
    public override CommandType CommandType
    {
        get => CommandType.Text;
        set { if (value != CommandType.Text) throw new NotSupportedException("Use SQL text, including CALL for procedures."); }
    }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value is null ? null : value as SdpConnection ??
            throw new ArgumentException("The command requires SdpConnection.");
    }
    protected override DbTransaction? DbTransaction
    {
        get => _transaction;
        set => _transaction = value is null ? null : value as SdpTransaction ??
            throw new ArgumentException("The command requires SdpTransaction.");
    }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbParameter CreateDbParameter() => new SdpParameter();

    public string? RequestToken { get; set; }
    public string? LastRequestToken { get; private set; }
    internal IReadOnlyList<SdpParameter> BoundParameters => _parameters.Items;

    public override void Prepare()
    {
        PrepareOnceAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public
#if !NETFRAMEWORK
        override
#endif
        Task PrepareAsync(CancellationToken cancellationToken = default) => PrepareOnceAsync(cancellationToken);

    private Task PrepareOnceAsync(CancellationToken token)
    {
        var connection = GetConnection();
        return connection.LockAsync(async () =>
        {
            Validate(connection);
            var declarations = BindParameters().Types;
            var wire = connection.RequireWire();
            await DuringExchangeAsync(connection, wire, token, async ct =>
            {
                var result = await wire.CallAsync("query.prepare", new { sql = CommandText, parameter_types = declarations }, ct).ConfigureAwait(false);
                var id = result.GetProperty("statement_id").GetString();
                await wire.CallAsync("statement_close", new { statement_id = id }, ct).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            return true;
        }, token);
    }

    public override void Cancel()
    {
        var running = Volatile.Read(ref _running);
        if (running is null) return;
        try
        {
            var id = running.Wire.CurrentRequestId;
            if (id != 0 && running.Connection.CancelAsync(running.Wire, id).GetAwaiter().GetResult()) return;
            if (Volatile.Read(ref _running) == running && (id == 0 || running.Wire.CurrentRequestId == id))
                running.Source.Cancel();
        }
        catch (Exception e) when (e is SdpException or ObjectDisposedException or OperationCanceledException)
        {
            if (Volatile.Read(ref _running) == running)
            {
                try { running.Source.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
    }

    internal async Task<T> DuringExchangeAsync<T>(SdpConnection connection, SdpWire wire, CancellationToken token,
        Func<CancellationToken, Task<T>> read)
    {
        using var timeout = new CancellationTokenSource();
        if (CommandTimeout > 0)
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(CommandTimeout, 86_400)));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        var running = new Running(connection, wire, linked);
        if (Interlocked.CompareExchange(ref _running, running, null) is not null)
            throw new InvalidOperationException("This command is already executing.");
        try { return await read(linked.Token).ConfigureAwait(false); }
        catch (SdpException e) when (e.Code == 1009 && timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new SdpException(1008, "HYT00", "Command timed out", e.UnknownOutcome, e.RequestToken, e.CorrelationId, inner: e);
        }
        finally { Interlocked.CompareExchange(ref _running, null, running); }
    }

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        do { while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { } }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        return reader.RecordsAffected;
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteAsync(CommandBehavior.Default, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.FieldCount > 0 ? reader.GetValue(0) : null;
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken) =>
        await ExecuteAsync(behavior, cancellationToken).ConfigureAwait(false);

    private Task<SdpDataReader> ExecuteAsync(CommandBehavior behavior, CancellationToken token)
    {
        const CommandBehavior supported = CommandBehavior.Default | CommandBehavior.CloseConnection | CommandBehavior.SequentialAccess;
        if ((behavior & ~supported) != 0) throw new NotSupportedException("This CommandBehavior is unsupported.");
        var connection = GetConnection();
        return connection.LockAsync(async () =>
        {
            Validate(connection);
            var wire = connection.RequireWire();
            var bindings = BindParameters();
            var mode = ModeOf(CommandText);
            if (RequestToken is not null && (connection.Transaction is not null || mode == StatementMode.Read))
                throw new InvalidOperationException("A request token can only be supplied for an autocommit write or CALL.");
            var ownsRoutine = mode == StatementMode.Routine && connection.Transaction is null;
            var tokenForWrite = connection.Transaction is null && mode != StatementMode.Read ?
                RequestToken is null ? SdpConnection.NewToken() : SdpConnection.ValidateToken(RequestToken) : null;
            LastRequestToken = tokenForWrite;
            string? prepared = null;
            var started = false;
            try
            {
                return await DuringExchangeAsync(connection, wire, token, async ct =>
                {
                    if (ownsRoutine)
                    {
                        await wire.CallAsync("tx_begin", null, ct).ConfigureAwait(false);
                        started = true;
                    }
                    var preparation = await wire.CallAsync("query.prepare",
                        new { sql = CommandText, parameter_types = bindings.Types }, ct).ConfigureAwait(false);
                    prepared = preparation.GetProperty("statement_id").GetString() ?? throw new JsonException("Missing statement ID.");
                    var args = new Dictionary<string, object?> { ["statement_id"] = prepared, ["parameters"] = bindings.Values };
                    var operationToken = ownsRoutine ? null : tokenForWrite;
                    if (operationToken is not null) args["request_token"] = operationToken;
                    var result = await wire.CallAsync("execute", args, ct, operationToken).ConfigureAwait(false);
                    var reader = new SdpDataReader(connection, this, prepared, result, ownsRoutine ? tokenForWrite : null,
                        (behavior & CommandBehavior.CloseConnection) != 0);
                    connection.ActiveReader = reader;
                    return reader;
                }).ConfigureAwait(false);
            }
            catch
            {
                if (wire.IsOpen)
                {
                    try
                    {
                        if (prepared is not null) await wire.CallAsync("statement_close", new { statement_id = prepared }, CancellationToken.None).ConfigureAwait(false);
                        if (started) await wire.CallAsync("tx_rollback", null, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (SdpException) { wire.Dispose(); }
                }
                throw;
            }
        }, token);
    }

    private void Validate(SdpConnection connection)
    {
        connection.RequireNoResult();
        if (!ReferenceEquals(connection.Transaction, _transaction))
            throw new InvalidOperationException("Set the command transaction to the connection's active transaction.");
        if (string.IsNullOrWhiteSpace(CommandText)) throw new ArgumentException("A value is required.", nameof(CommandText));
    }

    private SdpConnection GetConnection() => _connection ?? throw new InvalidOperationException("Set the command connection.");

    private (Dictionary<string, object> Types, Dictionary<string, object> Values) BindParameters()
    {
        var types = new Dictionary<string, object>(StringComparer.Ordinal);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var parameter in _parameters.Items)
        {
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.ReturnValue) continue;
            var name = BindName(parameter.ParameterName);
            var binding = parameter.Bind();
            if (types.ContainsKey(name)) throw new ArgumentException("Duplicate SQL parameter name.");
            types.Add(name, binding.Declaration);
            values.Add(name, binding.Value);
        }
        return (types, values);
    }

    internal static string BindName(string name) => SdpParameter.CanonicalName(name, true);

    private enum StatementMode { Read, Write, Routine }

    private static StatementMode ModeOf(string sql)
    {
        var rest = sql.AsSpan().TrimStart();
        while (rest.StartsWith("--", StringComparison.Ordinal) || rest.StartsWith("/*", StringComparison.Ordinal))
        {
            var end = rest.StartsWith("--", StringComparison.Ordinal) ?
                rest.IndexOfAny('\r', '\n') : rest.IndexOf("*/", StringComparison.Ordinal);
            if (end < 0) throw new ArgumentException("SQL is missing after a comment.", nameof(sql));
            rest = rest.Slice(end + (rest[1] == '*' ? 2 : 1)).TrimStart();
        }
        var length = 0;
        while (length < rest.Length && (rest[length] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) length++;
        var verb = rest.Slice(0, length).ToString().ToUpperInvariant();
        return verb switch
        {
            "SELECT" or "WITH" or "EXPLAIN" or "HISTORY" => StatementMode.Read,
            "CALL" or "DECLARE" or "BEGIN" => StatementMode.Routine,
            "CREATE" or "ALTER" or "DROP" or "INSERT" or "UPDATE" or "DELETE" or "TRUNCATE" or "GRANT"
                or "REVOKE" or "ANALYZE" or "REINDEX" => StatementMode.Write,
            _ => throw new NotSupportedException("Use the provider's typed operations for administrative commands.")
        };
    }
}