using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace StarDataParadise.SDK;

public sealed class SdpTransaction : DbTransaction
{
    private SdpConnection? _connection;

    internal SdpTransaction(SdpConnection connection) => _connection = connection;
    internal void Detach() => _connection = null;
    protected override DbConnection? DbConnection => _connection;
    public override IsolationLevel IsolationLevel => IsolationLevel.Serializable;
    public
#if !NETFRAMEWORK
        override
#endif
        bool SupportsSavepoints => true;
    public string? LastRequestToken { get; private set; }

    public override void Commit() => CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override void Rollback() => RollbackAsync(CancellationToken.None).GetAwaiter().GetResult();
    public
#if !NETFRAMEWORK
        override
#endif
        void Save(string name) => SaveAsync(name).GetAwaiter().GetResult();
    public
#if !NETFRAMEWORK
        override
#endif
        void Rollback(string name) => RollbackAsync(name).GetAwaiter().GetResult();
    public
#if !NETFRAMEWORK
        override
#endif
        void Release(string name) => ReleaseAsync(name).GetAwaiter().GetResult();

    public
#if !NETFRAMEWORK
        override
#endif
        Task CommitAsync(CancellationToken cancellationToken = default) =>
        FinishAsync(commit: true, null, cancellationToken);

    public Task CommitWithTokenAsync(string requestToken, CancellationToken cancellationToken = default) =>
        FinishAsync(commit: true, SdpConnection.ValidateToken(requestToken), cancellationToken);

    public
#if !NETFRAMEWORK
        override
#endif
        Task RollbackAsync(CancellationToken cancellationToken = default) =>
        FinishAsync(commit: false, null, cancellationToken);

    public
#if !NETFRAMEWORK
        override
#endif
        Task SaveAsync(string name, CancellationToken cancellationToken = default) =>
        SavepointAsync("tx_savepoint", name, cancellationToken);
    public
#if !NETFRAMEWORK
        override
#endif
        Task RollbackAsync(string name, CancellationToken cancellationToken = default) =>
        SavepointAsync("tx_rollback_to", name, cancellationToken);
    public
#if !NETFRAMEWORK
        override
#endif
        Task ReleaseAsync(string name, CancellationToken cancellationToken = default) =>
        SavepointAsync("tx_release", name, cancellationToken);

    private Task FinishAsync(bool commit, string? requestToken, CancellationToken cancellationToken)
    {
        var connection = _connection ?? throw new InvalidOperationException("The transaction has ended.");
        return connection.LockAsync(async () =>
        {
            Validate(connection);
            var wire = connection.RequireWire();
            var key = commit ? requestToken ?? SdpConnection.NewToken() : null;
            LastRequestToken = key;
            try
            {
                if (commit)
                    await wire.CallAsync("tx_commit", new { request_token = key }, cancellationToken, key).ConfigureAwait(false);
                else
                    await wire.CallAsync("tx_rollback", null, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (wire.IsOpen)
                {
                    try { await wire.CallAsync("tx_rollback", null, CancellationToken.None).ConfigureAwait(false); }
                    catch (SdpException) { wire.Dispose(); }
                }
                throw;
            }
            finally { End(connection); }
            return true;
        }, cancellationToken);
    }

    private Task SavepointAsync(string operation, string name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A value is required.", nameof(name));
        var connection = _connection ?? throw new InvalidOperationException("The transaction has ended.");
        return connection.LockAsync(async () =>
        {
            Validate(connection);
            await connection.RequireWire().CallAsync(operation, new { name }, cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }

    private void Validate(SdpConnection connection)
    {
        if (connection.Transaction != this) throw new InvalidOperationException("The transaction has ended.");
        connection.RequireNoResult();
    }

    private void End(SdpConnection connection)
    {
        connection.Transaction = null;
        Detach();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _connection is { } connection)
        {
            if (connection.State == ConnectionState.Open)
            {
                try { Rollback(); }
                catch (SdpException) { connection.Close(); }
                catch (InvalidOperationException) { connection.Close(); }
            }
            else End(connection);
        }
        base.Dispose(disposing);
    }

    public
#if !NETFRAMEWORK
        override
#endif
        async ValueTask DisposeAsync()
    {
        if (_connection is { } connection)
        {
            if (connection.State == ConnectionState.Open)
            {
                try { await RollbackAsync().ConfigureAwait(false); }
                catch (SdpException) { await connection.CloseAsync().ConfigureAwait(false); }
                catch (InvalidOperationException) { await connection.CloseAsync().ConfigureAwait(false); }
            }
            else End(connection);
        }
        GC.SuppressFinalize(this);
    }
}