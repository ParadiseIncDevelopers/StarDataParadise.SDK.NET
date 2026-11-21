# StarDataParadiseNetFramework

2.0.0 is the ADO.NET provider for StarDataParadise Server 0.3.0 and later servers speaking wire v1. The package targets .NET Framework 4.8 and uses TLS 1.2. Visual Studio 2019 consumers can target .NET Framework 4.8 with its targeting pack.

The 2020 package 1.0.6 exposed the legacy node API and targeted .NET Standard 2.0. Applications that use those types can keep their explicit 1.0.6 reference. The 2.0 release uses `StarDataParadise.SDK.SdpConnection`, SQL parameters, transactions and named procedure result sets. Connection and repository settings are supplied by the application.

~~~xml
<PackageReference Include="StarDataParadiseNetFramework" Version="2.0.0" />
~~~

Create connections with `SdpFactory.Instance`, or register the factory through the application's configuration.

~~~xml
<system.data>
  <DbProviderFactories>
    <remove invariant="StarDataParadise.SDK.NET" />
    <add name="StarDataParadise" invariant="StarDataParadise.SDK.NET"
         description="StarDataParadise ADO.NET provider"
         type="StarDataParadise.SDK.SdpFactory, StarDataParadise.SDK.NET" />
  </DbProviderFactories>
</system.data>
~~~

~~~csharp
using StarDataParadise.SDK;

using (var connection = new SdpConnection(connectionString))
{
    connection.Open();
    using (var command = connection.CreateCommand())
    {
        command.CommandText = "SELECT id FROM items WHERE id=:id";
        command.Parameters.Add(new SdpParameter("id", 42L));
        using (var rows = command.ExecuteReader())
        {
            while (rows.Read())
                System.Console.WriteLine(rows.GetInt64(0));
        }
    }
}
~~~

DATE values use `DateTime` with unspecified time zone. TIME and TIMESTAMP retain nanoseconds in `SdpTime` and `SdpTimestamp`. Exact decimals use `SdpDecimal`. Savepoint and async convenience methods are available on concrete provider types. Use `SdpFactory.Instance` or app.config factory discovery for .NET Framework.

The contract archive is embedded with wire version 1 and SHA-256 `e6500900a3fa34910781ea92d5e41fe8716511fac6f6d6c689ff9cce15d578ab`. On an uncertain write result, resolve its request token with a new connection and `GetOutcomeAsync`.


## Native SQL sessions

Use `await SdpNativeConnection.OpenAsync(connectionString)` for Server 0.5.0 or later advertising `native_sql_v1`. This is a dedicated connection, shared by .NET 8, .NET 10 and .NET Framework 4.8. `Pooling=true` is rejected. The existing ADO.NET `SdpConnection` and its durable CRUD receipt protocol retain their behavior.

```csharp
using var session = await SdpNativeConnection.OpenAsync(connectionString);
var result = await session.ExecuteAsync(
    "SELECT CAST(? AS UNSIGNED), CAST(? AS JSON)",
    new object?[] { ulong.MaxValue, new SdpNativeJson("{\"id\":9007199254740993}") });
```

Positional `?` values go to the native prepared-statement protocol. SQL, comments and procedure bodies are passed to the server compiler unchanged. `SdpNativeJson` retains serialized JSON digits; `SdpNativeDecimal` supports 65 coefficient digits and scale 30; `SdpNativeTemporal` retains DATE/DATETIME/TIMESTAMP text and signed TIME intervals beyond 24 hours. Native binary values are byte arrays and unsigned BIGINT values are `ulong`. The result includes all sets, affected counts and the server's transaction/autocommit flags.

`BeginAsync`, `RollbackAsync`, `CommitAsync`, `FetchResponseAsync` and `CancelRunAsync` use native SQL. COMMIT request keys are optional native AI options, not legacy CRUD receipts. A lost execute/fetch acknowledgement sets `SdpException.UnknownOutcome` even without a request token. No write is automatically replayed. Preserve native `run_id` and `call_id` to inspect the completed AI turn and its action status. `Details`, `NativeErrno`, `Phase`, `CommitState`, `RunState`, `RunId`, `CallId`, `ActionCommitState`, `CauseNativeErrno`, `CauseSqlstate` and `Retry` preserve structured error evidence. `CommitState` describes the initial turn; `ActionCommitState` separately describes the tool action, including a known rollback after the initial turn committed. Transaction flags describe the last successful response.

Large responses are bounded to 16 MiB, transferred in bounded chunks and checked against SHA-256 before decoding. An incomplete/malformed result closes the native connection and reports an unknown operation outcome. Boundary tests in `native-smoke` cover framing, exact values, chunks/hash, transaction flags and lost acknowledgements; set `SDP_NATIVE_CONNECTION` to run its separate real SQL/TLS case. A passing wire fixture alone is not native server/compiler acceptance.
