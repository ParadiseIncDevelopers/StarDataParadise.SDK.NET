using System;
using System.Collections;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StarDataParadise.SDK;

public sealed class SdpDataReader : DbDataReader
{
    private readonly SdpConnection _connection;
    private readonly SdpCommand _command;
    private readonly string _statement;
    private readonly string? _result;
    private readonly string? _routineToken;
    private readonly bool _closeConnection;
    private JsonElement _columns;
    private JsonElement _rows;
    private JsonElement _current;
    private int _nextRow;
    private int _setIndex;
    private int _batchIndex;
    private bool _remoteResult;
    private bool _endSet;
    private bool _failed;
    private bool _closed;
    private bool _everHadRows;
    private string? _setName;

    internal SdpDataReader(SdpConnection connection, SdpCommand command, string statement, JsonElement execute,
        string? routineToken, bool closeConnection)
    {
        _connection = connection;
        _command = command;
        _statement = statement;
        _routineToken = routineToken;
        _closeConnection = closeConnection;
        _result = execute.TryGetProperty("result_id", out var resultId) ? resultId.GetString() : null;
        _remoteResult = _result is not null;
        _endSet = !_remoteResult;
        _columns = execute.TryGetProperty("columns", out var data) ? data : default;
        var count = execute.GetProperty("affected_rows").GetInt32();
        RecordsAffected = _remoteResult && count == 0 ? -1 : count;
    }

    public override int Depth => 0;
    public override bool IsClosed => _closed || _connection.State != ConnectionState.Open;
    public override int RecordsAffected { get; }
    public override int FieldCount => Length(_columns);
    public string? ResultSetName => _setName;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override bool HasRows => _connection.LockAsync(async () =>
    {
        ValidateActive();
        if (_batchIndex == 0 && !_endSet) await FetchAsync(CancellationToken.None).ConfigureAwait(false);
        return _everHadRows;
    }, CancellationToken.None).GetAwaiter().GetResult();

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
    public override bool NextResult() => NextResultAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override Task<bool> ReadAsync(CancellationToken cancellationToken) => _connection.LockAsync(async () =>
    {
        ValidateActive();
        _current = default;
        while (_nextRow >= Length(_rows) && !_endSet) await FetchAsync(cancellationToken).ConfigureAwait(false);
        if (_nextRow >= Length(_rows)) return false;
        _current = _rows[_nextRow++];
        return true;
    }, cancellationToken);

    public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => _connection.LockAsync(async () =>
    {
        ValidateActive();
        _current = default;
        while (!_endSet) await FetchAsync(cancellationToken).ConfigureAwait(false);
        if (!_remoteResult) return false;
        _setIndex++;
        _batchIndex = 0;
        _everHadRows = false;
        _setName = null;
        await FetchAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }, cancellationToken);

    private async Task FetchAsync(CancellationToken cancellationToken)
    {
        var wire = _connection.RequireWire();
        try
        {
            var reply = await _command.DuringExchangeAsync(_connection, wire, cancellationToken, token =>
                wire.CallAsync("fetch", new { result_id = _result, batch_rows = 128 }, token)).ConfigureAwait(false);
            var batch = reply.GetProperty("batch");
            if (batch.GetProperty("result_set").GetInt32() != _setIndex ||
                batch.GetProperty("batch_index").GetInt32() != _batchIndex)
                throw new SdpException(1001, "08P01", "Result batches are out of sequence.");
            if (_batchIndex++ == 0)
            {
                _columns = batch.GetProperty("columns");
                _setName = batch.TryGetProperty("set_name", out var label) ? label.GetString() : null;
            }
            _rows = batch.GetProperty("rows");
            _endSet = batch.GetProperty("end_of_set").GetBoolean();
            var endResults = batch.GetProperty("end_of_results").GetBoolean();
            if (endResults && !_endSet || !_endSet && Length(_rows) == 0)
                throw new SdpException(1001, "08P01", "A result batch did not advance.");
            _remoteResult = !endResults;
            _nextRow = 0;
            _everHadRows |= Length(_rows) > 0;
            foreach (var row in _rows.EnumerateArray())
                if (Length(row) != FieldCount)
                    throw new SdpException(1001, "08P01", "A result row does not match the column metadata.");
            if (_setName == "out" && Length(_rows) != 0) CaptureOutput(_rows[0]);
        }
        catch
        {
            _failed = true;
            _endSet = true;
            throw;
        }
    }

    private void CaptureOutput(JsonElement row)
    {
        foreach (var parameter in _command.BoundParameters.Where(p => p.Direction != ParameterDirection.Input))
        {
            var name = SdpCommand.BindName(parameter.ParameterName);
            for (var column = 0; column < FieldCount; column++)
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(name, GetName(column))) continue;
                parameter.Value = SdpValues.Decode(row[column], GetDataTypeName(column));
                break;
            }
        }
    }

    private static int Length(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.GetArrayLength() : 0;
    private JsonElement Column(int ordinal)
    {
        if (ordinal < 0 || ordinal >= FieldCount) throw new IndexOutOfRangeException();
        return _columns[ordinal];
    }

    public override string GetName(int ordinal) => Column(ordinal).GetProperty("name").GetString()!;
    public override string GetDataTypeName(int ordinal) => Column(ordinal).GetProperty("type").GetString()!;
    public override Type GetFieldType(int ordinal) => SdpValues.ClrType(GetDataTypeName(ordinal));
    public override int GetOrdinal(string name)
    {
        for (var index = 0; index < FieldCount; index++)
            if (StringComparer.Ordinal.Equals(GetName(index), name)) return index;
        for (var index = 0; index < FieldCount; index++)
            if (StringComparer.OrdinalIgnoreCase.Equals(GetName(index), name)) return index;
        throw new IndexOutOfRangeException("The column does not exist.");
    }

    public override object GetValue(int ordinal)
    {
        var type = GetDataTypeName(ordinal);
        if (_current.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("Read a result row first.");
        return SdpValues.Decode(_current[ordinal], type);
    }

    public override int GetValues(object[] values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++) values[i] = GetValue(i);
        return count;
    }

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is T typed) return typed;
        if (typeof(T) == typeof(decimal) && value is SdpDecimal number) return (T)(object)number.ToDecimal();
        if (typeof(T) == typeof(DateTimeOffset) && value is SdpTimestamp instant) return (T)(object)instant.ToDateTimeOffset();
#if !NETFRAMEWORK
        if (typeof(T) == typeof(TimeOnly) && value is SdpTime time) return (T)(object)time.ToTimeOnly();
#endif
        throw new InvalidCastException("The column cannot be represented by the requested CLR type.");
    }

    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    public override bool GetBoolean(int ordinal) => GetFieldValue<bool>(ordinal);
    public override byte GetByte(int ordinal) => checked((byte)GetInt64(ordinal));
    public override char GetChar(int ordinal)
    {
        var text = GetString(ordinal);
        return text.Length == 1 ? text[0] : throw new InvalidCastException("The column is not a single UTF-16 code unit.");
    }
    public override DateTime GetDateTime(int ordinal) => GetValue(ordinal) switch
    {
#if NETFRAMEWORK
        DateTime date => date,
#else
        DateOnly date => date.ToDateTime(TimeOnly.MinValue),
#endif
        SdpTimestamp stamp => stamp.ToDateTimeOffset().DateTime,
        _ => throw new InvalidCastException()
    };
    public override decimal GetDecimal(int ordinal) => GetFieldValue<decimal>(ordinal);
    public override double GetDouble(int ordinal)
    {
        var value = GetValue(ordinal);
        return Convert.ToDouble(value is SdpDecimal exact ? exact.ToDecimal() : value, CultureInfo.InvariantCulture);
    }
    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));
    public override short GetInt16(int ordinal) => checked((short)GetInt64(ordinal));
    public override int GetInt32(int ordinal) => checked((int)GetInt64(ordinal));
    public override long GetInt64(int ordinal) => GetFieldValue<long>(ordinal);
    public override string GetString(int ordinal) => GetFieldValue<string>(ordinal);
    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        CopyValue(GetFieldValue<byte[]>(ordinal), dataOffset, buffer, bufferOffset, length);

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        CopyValue(GetString(ordinal).ToCharArray(), dataOffset, buffer, bufferOffset, length);

    private static long CopyValue<T>(T[] source, long offset, T[]? buffer, int bufferOffset, int length)
    {
        if (buffer is null) return source.LongLength;
        if (offset < 0 || offset > source.LongLength) throw new ArgumentOutOfRangeException(nameof(offset));
        if (bufferOffset < 0 || length < 0 || bufferOffset > buffer.Length || length > buffer.Length - bufferOffset)
            throw new ArgumentOutOfRangeException(nameof(bufferOffset));
        var count = (int)Math.Min(length, source.LongLength - offset);
        Array.Copy(source, (int)offset, buffer, bufferOffset, count);
        return count;
    }

    public override DataTable GetSchemaTable()
    {
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("ColumnOrdinal", typeof(int));
        table.Columns.Add("DataType", typeof(Type));
        table.Columns.Add("DataTypeName", typeof(string));
        for (var i = 0; i < FieldCount; i++) table.Rows.Add(GetName(i), i, GetFieldType(i), GetDataTypeName(i));
        return table;
    }

    private void ValidateActive()
    {
        if (IsClosed || _connection.ActiveReader != this)
            throw new InvalidOperationException("The result reader is closed.");
    }

    public override void Close() => CloseAsync().GetAwaiter().GetResult();
    public
#if !NETFRAMEWORK
        override
#endif
        async Task CloseAsync()
    {
        if (_closed) return;
        try
        {
            await _connection.LockAsync(async () =>
            {
                await FinishAsync(commitRoutine: true, CancellationToken.None).ConfigureAwait(false);
                return true;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (_closeConnection) await _connection.CloseAsync().ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Close();
        base.Dispose(disposing);
    }

    public
#if !NETFRAMEWORK
        override
#endif
        async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    internal async Task FinishAsync(bool commitRoutine, CancellationToken cancellationToken)
    {
        if (_closed) return;
        try
        {
            if (_connection.State != ConnectionState.Open)
                return;
            var wire = _connection.RequireWire();
            try
            {
                if (commitRoutine && !_failed && _batchIndex == 0 && _remoteResult &&
                    _command.BoundParameters.Any(p => p.Direction != ParameterDirection.Input))
                    await FetchAsync(cancellationToken).ConfigureAwait(false);
                if (_remoteResult && !_failed)
                    await wire.CallAsync("result_cancel", new { result_id = _result }, cancellationToken).ConfigureAwait(false);
                await wire.CallAsync("statement_close", new { statement_id = _statement }, cancellationToken).ConfigureAwait(false);
                if (_routineToken is not null)
                {
                    if (commitRoutine && !_failed)
                        await wire.CallAsync("tx_commit", new { request_token = _routineToken }, cancellationToken, _routineToken).ConfigureAwait(false);
                    else
                        await wire.CallAsync("tx_rollback", null, cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                wire.Dispose();
                throw;
            }
        }
        finally
        {
            _closed = true;
            _current = default;
            if (_connection.ActiveReader == this) _connection.ActiveReader = null;
        }
    }
}