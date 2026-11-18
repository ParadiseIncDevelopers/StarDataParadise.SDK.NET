using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using StarDataParadise.SDK;

static void Check(bool value, string step)
{
    if (!value) throw new InvalidOperationException(step);
}

static string HashHex(byte[] bytes)
{
    using var sha = SHA256.Create();
    return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
}

static string Env(string name) => Environment.GetEnvironmentVariable("SDP_SDK_" + name)
    ?? throw new InvalidOperationException("Missing fixture configuration " + name);

static SdpConnection Connect(string user, string password, int port, bool pooling = false)
{
    var settings = SdpFactory.Instance.CreateConnectionStringBuilder();
    settings["Host"] = Env("HOST");
    settings["Port"] = port;
    settings["TLS Name"] = Env("TLS_NAME");
    settings["CA File"] = Env("CA");
    settings["User"] = user;
    settings["Password"] = password;
    settings["Pooling"] = pooling;
    settings["Timeout"] = 12;
    return new SdpConnection(settings.ConnectionString);
}

static SdpCommand Command(SdpConnection connection, string sql, SdpTransaction? transaction = null)
{
    var command = (SdpCommand)connection.CreateCommand();
    command.CommandText = sql;
    command.Transaction = transaction;
    return command;
}

static async Task<int> Write(SdpConnection connection, string sql, SdpTransaction? transaction = null)
{
    using var command = Command(connection, sql, transaction);
    return await command.ExecuteNonQueryAsync();
}

static async Task<object?> Scalar(SdpConnection connection, string sql, SdpTransaction? transaction = null)
{
    using var command = Command(connection, sql, transaction);
    return await command.ExecuteScalarAsync();
}

static async Task CheckError(int code, Func<Task> action)
{
    try { await action(); }
    catch (SdpException error) when (error.Code == code)
    {
        Check(error.SqlState.Length == 5 && !error.UnknownOutcome, "Definite error shape");
        return;
    }
    throw new InvalidOperationException("The expected definite SQLSTATE was absent.");
}

static async Task CheckConnectionOptions(string options, string expectedMessage)
{
    using var connection = new SdpConnection($"{options};User=local_test;Password=local_test;");
    try { await connection.OpenAsync(); }
    catch (ArgumentException error) when (error.Message == expectedMessage) { return; }
    throw new InvalidOperationException("The connection options should have been rejected before connecting.");
}

static void Golden()
{
    using var stream = SdpContract.OpenArchive();
    using var bytes = new MemoryStream();
    stream.CopyTo(bytes);
    Check(HashHex(bytes.ToArray()).Equals(Env("CONTRACT_SHA256"), StringComparison.OrdinalIgnoreCase),
        "Installed contract archive digest");
    bytes.Position = 0;
    using var archive = new ZipArchive(bytes, ZipArchiveMode.Read);
    using var content = archive.GetEntry("golden/values.json")!.Open();
    using var original = JsonDocument.Parse(content);
    using var fixture = JsonDocument.Parse(File.ReadAllBytes(Env("GOLDEN")));
    var raw = fixture.RootElement.GetProperty("body").GetProperty("values");
    Check(original.RootElement.GetProperty("body").GetProperty("values").GetRawText() == raw.GetRawText(), "Golden fixture agreement");
    var decoded = raw.EnumerateArray().Select(SdpContract.DecodeValue).ToArray();
    Check(decoded[0] is SdpDecimal { Scale: 4 } number && number.Unscaled == BigInteger.Parse("-123456789012345678901234567890123400"),
        "Golden decimal");
    Check(decoded[1] is DBNull && (string)decoded[2] == "" && ((string)decoded[3]).Contains("İstanbul")
        && ((byte[])decoded[4]).SequenceEqual(new byte[] { 0, 1, 127, 128, 254, 255 }), "Golden null, empty, Unicode and bytes");
    Check((long)decoded[5] == long.MinValue && (long)decoded[6] == long.MaxValue && (string)decoded[7] == "a\0b",
        "Golden bounds and embedded NUL");
    Check(decoded[8] is SdpTimestamp { Seconds: -1, Nanos: 999999999, OffsetMinutes: 180, Zone: "Europe/Istanbul" },
        "Golden timestamp");
    Check(decoded[9] is SdpRecord record && record[0].Key == "null" && record[0].Value is DBNull &&
        record[1].Key == "empty" && (string?)record[1].Value == "", "Golden ordered record");
}

Check(SdkVersion.AssemblyVersion.Major == 2 && SdpContract.WireVersion == 1, "Installed provider version");
#if !NETFRAMEWORK
SdpFactory.Register();
#endif
Check(DbProviderFactories.GetFactory(SdpFactory.InvariantName) is SdpFactory, "ADO.NET provider factory");
await CheckConnectionOptions("Host=192.0.2.1;TLS Mode=Disabled",
    "TLS Mode=Disabled requires localhost or a loopback IP address.");
await CheckConnectionOptions("Host=localhost.example;TLS Mode=Disabled",
    "TLS Mode=Disabled requires localhost or a loopback IP address.");
await CheckConnectionOptions("Host=localhost;TLS Mode=Disabled;CA File=cert.pem",
    "CA File and TLS Name require TLS Mode=Required.");
await CheckConnectionOptions("Host=localhost;TLS Mode=Preferred",
    "TLS Mode must be Required or Disabled.");
if (Environment.GetEnvironmentVariable("SDP_SDK_PORT") is null)
{
    Console.WriteLine($"StarDataParadise SDK {SdkVersion.AssemblyVersion} package loaded for {AppContext.TargetFrameworkName}");
    return;
}

Golden();
var owner = Env("USER");
var ownerPassword = Console.ReadLine() ?? throw new InvalidOperationException("The fixture omitted the first credential.");
var guest = Env("GUEST");
var guestPassword = Console.ReadLine() ?? throw new InvalidOperationException("The fixture omitted the second credential.");
var port = int.Parse(Env("PORT"));
using var connection = Connect(owner, ownerPassword, port, pooling: true);
await connection.OpenAsync();
Check(!connection.ConnectionString.Contains(ownerPassword, StringComparison.Ordinal), "Connection string redaction");
var features = (await connection.GetCapabilitiesAsync()).GetProperty("features");
foreach (var feature in new[] { "sql_crud_v1", "plsql_results_v1", "history_v1", "admin_v1" })
    Check(features.GetProperty(feature).GetBoolean(), "Expected capability " + feature);
Check((await connection.GetHealthAsync()).GetProperty("status").GetString() == "ready", "Independent TLS health");

using (var types = Command(connection, "SELECT note,amount,stamp,payload FROM sdk_seed ORDER BY id"))
using (var reader = await types.ExecuteReaderAsync())
{
    Check(reader.HasRows && reader.FieldCount == 4 && reader.GetFieldType(1) == typeof(SdpDecimal), "SQL field metadata");
    Check(await reader.ReadAsync(), "Typed row exists");
    Check(reader.GetString(0) == "İstanbul'un notu; a\0b", "Unicode/NUL");
    var number = reader.GetFieldValue<SdpDecimal>(1);
    Check(number.Unscaled == BigInteger.Parse("-123456789012345678901234567890123400") && number.Scale == 4, "Exact fixed decimal");
    try { _ = reader.GetDecimal(1); throw new InvalidOperationException("CLR decimal silently rounded."); }
    catch (OverflowException) { }
    var timestamp = reader.GetFieldValue<SdpTimestamp>(2);
    Check(timestamp is { Seconds: -1, Nanos: 999999999, OffsetMinutes: 180 }, "Timestamp precision and offset");
    Check(reader.GetFieldValue<byte[]>(3).SequenceEqual(new byte[] { 0, 1, 127, 128, 254, 255 }), "Binary values");
    Check(await reader.ReadAsync() && reader.IsDBNull(0) && reader.IsDBNull(1) && reader.IsDBNull(2) && reader.IsDBNull(3),
        "SQL NULL values");
    Check(!await reader.ReadAsync(), "Typed query row boundary");
}

Check(await Write(connection, "CREATE TABLE dotnet_crud (id BIGINT PRIMARY KEY, note TEXT)") == 0, "DDL");
const string injection = "x'); DELETE FROM sdk_seed; --";
using (var command = Command(connection, "INSERT INTO dotnet_crud VALUES (:id,:value)"))
{
    command.Parameters.Add(new SdpParameter("id", 10L));
    command.Parameters.Add(new SdpParameter(":value", injection));
    await command.PrepareAsync();
    Check(await command.ExecuteNonQueryAsync() == 1, "Parameterized insert");
    Check(command.LastRequestToken is { Length: 32 }, "Write outcome key");
}
Check((string?)await Scalar(connection, "SELECT note FROM dotnet_crud WHERE id=10") == injection, "Injection stored as data");
using (var command = Command(connection, "UPDATE dotnet_crud SET note=:value WHERE id=:id"))
{
    command.Parameters.Add(new SdpParameter("id", 10L));
    command.Parameters.Add(new SdpParameter("value", "updated"));
    Check(await command.ExecuteNonQueryAsync() == 1, "Parameterized update");
}
Check((string?)await Scalar(connection, "SELECT note FROM dotnet_crud WHERE id=10") == "updated", "Verified update");
using (var command = Command(connection, "DELETE FROM dotnet_crud WHERE id=:id"))
{
    command.Parameters.Add(new SdpParameter("id", 10L));
    Check(await command.ExecuteNonQueryAsync() == 1, "Parameterized delete");
}
Check(await Scalar(connection, "SELECT id FROM dotnet_crud WHERE id=10") is null, "Verified deletion");

using (var command = Command(connection, "CALL sdk_list(:key,NULL)"))
{
    command.Parameters.Add(new SdpParameter("key", 1L));
    var output = new SdpParameter("p_value", DbType.Int64) { Direction = ParameterDirection.Output };
    command.Parameters.Add(output);
    using var rawReader = await command.ExecuteReaderAsync();
    var reader = (SdpDataReader)rawReader;
    Check(await reader.ReadAsync() && reader.ResultSetName == "out" && reader.GetInt64(0) == 1, "Procedure OUT set");
    Check(await reader.NextResultAsync() && reader.ResultSetName == "found" && await reader.ReadAsync() && reader.GetInt64(0) == 1,
        "Procedure cursor set");
    Check(await reader.NextResultAsync() && reader.ResultSetName == "all_rows" && await reader.ReadAsync() && reader.GetInt64(0) == 1
        && await reader.ReadAsync() && reader.GetInt64(0) == 2 && !await reader.NextResultAsync(), "Procedure final cursor set");
    Check(output.Value is 1L, "Bound output parameter");
}
using (var command = Command(connection, "CALL sdk_echo(:p_rec,:p_values)"))
{
    var input = new SdpRecord(new Dictionary<string, object?> { ["first"] = 41L, ["second"] = null });
    var contents = new SdpCollection(new object?[] { true, input });
    var record = new SdpParameter("p_rec", input) { Direction = ParameterDirection.InputOutput };
    var collection = new SdpParameter("p_values", contents) { Direction = ParameterDirection.InputOutput };
    command.Parameters.Add(record);
    command.Parameters.Add(collection);
    await command.ExecuteNonQueryAsync();
    Check(record.Value is SdpRecord recovered && recovered.Count == 2 && recovered[0].Value is 41L &&
        collection.Value is SdpCollection recoveredCollection && recoveredCollection[0] is true &&
        recoveredCollection[1] is SdpRecord nested && nested[1].Value is DBNull, "Compound IN OUT values");
}

using (var tx = (SdpTransaction)connection.BeginTransaction())
{
    await tx.SaveAsync("drop_write");
    await Write(connection, "INSERT INTO dotnet_crud VALUES (11,'discard')", tx);
    await tx.RollbackAsync("drop_write");
    await tx.ReleaseAsync("drop_write");
    Check(await Scalar(connection, "SELECT id FROM dotnet_crud WHERE id=11", tx) is null, "Rollback to savepoint");
    await Write(connection, "INSERT INTO dotnet_crud VALUES (12,'discard')", tx);
    await tx.RollbackAsync();
}
Check(await Scalar(connection, "SELECT id FROM dotnet_crud WHERE id=12") is null, "Explicit rollback");

using (var bulk = Command(connection, "SELECT id FROM sdk_bulk ORDER BY id"))
using (var reader = await bulk.ExecuteReaderAsync(CommandBehavior.SequentialAccess))
{
    var rows = 0;
    while (await reader.ReadAsync()) Check(reader.GetInt64(0) == ++rows, "Batch order");
    Check(rows == int.Parse(Env("BULK_ROWS")), "Bounded result streamed across batches");
}
Check((await connection.GetHistoryAsync("sdk_seed")).GetProperty("entries").GetArrayLength() > 0, "History typed access");
var predicted = await connection.PredictAsync(Env("MODEL"), new Dictionary<string, object?> { ["reading"] = 17 });
Check(Math.Abs(predicted.GetProperty("prediction").GetDouble() - 58) < .0001, "Model inference");
Check((await connection.GetJobAsync(Env("JOB_ID"))).GetProperty("state").GetString() == "succeeded", "Persisted job");
Check((await connection.GetDatasetAsync(Env("DATASET_ID"))).GetProperty("row_count").GetInt32() == int.Parse(Env("BULK_ROWS")), "Dataset");
Check((await connection.GetAdminHealthAsync()).TryGetProperty("instance_id", out _), "Admin health");
var decision = await connection.ValidateDecisionAsync("CREATE DECISION RULE observer ON sdk_seed FOR UPDATE PRIORITY 1 WHEN (NEW.id > 0) THEN DENY");
Check(decision.GetProperty("valid").GetBoolean(), "Decision validation used the Server parser");

var session = connection.SessionId;
using (var transaction = (SdpTransaction)connection.BeginTransaction())
{
    await Write(connection, "INSERT INTO dotnet_crud VALUES (13,'discard pool transaction')", transaction);
    await connection.CloseAsync();
}
using (var limited = Connect(guest, guestPassword, port, pooling: true))
{
    await limited.OpenAsync();
    Check(limited.SessionId != session, "Guest did not borrow the administrator's authenticated stream");
    Check(await Scalar(limited, "SELECT id FROM sdk_seed WHERE id=1") is 1L, "Guest SELECT grant");
    await CheckError(1006, () => limited.GetHistoryAsync("sdk_seed"));
    await CheckError(1006, () => limited.PredictAsync(Env("MODEL"), new Dictionary<string, object?> { ["reading"] = 17 }));
}
await connection.OpenAsync();
Check(connection.SessionId == session && await Scalar(connection, "SELECT id FROM dotnet_crud WHERE id=13") is null,
    "Reset before reusing the identical authenticated physical session");
using (var victim = Connect(owner, ownerPassword, port))
{
    await victim.OpenAsync();
    using var holding = (SdpTransaction)connection.BeginTransaction();
    using var awaiting = Command(victim, "SELECT id FROM sdk_seed");
    var pending = awaiting.ExecuteScalarAsync();
    for (var retries = 0; retries < 30; retries++)
    {
        await Task.Delay(50);
        var sessions = (await connection.GetAdminStatusAsync()).GetProperty("sessions");
        if (sessions.EnumerateArray().Any(s => s.GetProperty("session_id").GetString() == victim.SessionId
            && s.GetProperty("request_id").ValueKind == JsonValueKind.Number)) break;
    }
    awaiting.Cancel();
    await CheckError(1009, () => pending);
    await holding.RollbackAsync();
    Check(await Scalar(victim, "SELECT 7") is 7L, "Server-side cancellation kept the authenticated session usable");
}

var receipt = Guid.NewGuid().ToString("N");
using (var faulted = Connect(owner, ownerPassword, int.Parse(Env("FAULT_PORT"))))
{
    await faulted.OpenAsync();
    using var transaction = (SdpTransaction)faulted.BeginTransaction();
    await Write(faulted, "INSERT INTO dotnet_crud VALUES (14,'commit acknowledgement lost')", transaction);
    try
    {
        await transaction.CommitWithTokenAsync(receipt);
        throw new InvalidOperationException("Commit transport did not lose the reply.");
    }
    catch (SdpException error) when (error.Code == 1010 && error.UnknownOutcome)
    {
        Check(error.RequestToken == receipt, "Uncertain commit retained the original token");
    }
}
Check((await connection.GetOutcomeAsync(receipt)).GetProperty("status").GetString() == "committed",
    "Cross-session durable status confirmed the original commit");
Check(await Scalar(connection, "SELECT id FROM dotnet_crud WHERE id=14") is 14L, "Durable row recorded once");

Console.WriteLine("SDP_MANAGED_OK");
