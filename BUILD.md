# StarDataParadiseDrive

StarDataParadiseDrive 2.0.3 is an independent ADO.NET provider for .NET 8 and .NET 10 on StarDataParadise Server 0.3.0 wire protocol v1. It uses TLS with certificate and server-name verification by default. The package has no Native DLL dependency. Existing 1.x applications should retain their pinned version until they migrate to .NET 8 or later and the provider API.

~~~powershell
dotnet add package StarDataParadiseDrive --version 2.0.3
~~~

## Connect and bind SQL parameters

~~~csharp
using System.Data;
using StarDataParadise.SDK;

var settings = SdpFactory.Instance.CreateConnectionStringBuilder();
settings["Host"] = "127.0.0.1";
settings["Port"] = 7443;
settings["TLS Name"] = "localhost";
settings["CA File"] = caPemPath;
settings["User"] = user;
settings["Password"] = password;

await using var connection = new SdpConnection(settings.ConnectionString);
await connection.OpenAsync(cancellationToken);
await using var command = connection.CreateCommand();
command.CommandText = "SELECT note FROM items WHERE id = :key";
command.Parameters.Add(new SdpParameter("key", 7L));
await using var reader = await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
    Console.WriteLine(reader.GetString(0));
~~~

Use SdpFactory.Register() if your application looks providers up by invariant name. The default database is main. Use the system TLS trust store or set CA File to a PEM trust bundle. The default connection timeout is 15 seconds. CommandTimeout bounds each production RPC performed by the command. The connection string returned after opening omits the password.

A wire v1 listener configured for local plaintext development accepts the following connection string. The same local endpoint can accept Classic TCP clients when configured in shared-port mode.

~~~text
Host=127.0.0.1;Port=3197;User=razor_sdk;Password=<local-test-password>;TLS Mode=Disabled;
~~~

The provider accepts TLS Mode=Disabled only for localhost or a loopback IP address. It connects directly to a loopback IP address without DNS and sends credentials and SQL over that connection in plaintext. Omit CA File and TLS Name when using this mode. TLS Mode=Required is the default.

Use DbType or SqlTypeName for typed NULL parameters. The result reader returns DBNull.Value, byte arrays, signed 64-bit integers and the full-precision SdpDecimal, SdpTimestamp, SdpTime, SdpRecord and SdpCollection types. Converting to a CLR decimal or timestamp reports overflow or precision loss. DATE returns DateOnly. A timestamp's UTC seconds, nanos and display offset are separate. The wire value codec preserves named zones, while Server 0.3.0 supports binding fixed-offset timestamps.

CALL takes typed IN and IN OUT parameters. Use SQL NULL for a pure OUT positional argument and a named SdpParameter with Direction=Output to receive the matching result column. Read and NextResult stream the named OUT and cursor sets. An autocommit CALL runs in one explicit Server transaction that completes when the result closes. Abort the connection or roll back an explicit transaction to discard a routine mutation. Concurrent results on a single connection, CommandType.StoredProcedure, and changing the current database are unsupported.

## Transaction and management API

Associate a command with the active SdpTransaction. Transactions are serializable. Save, Rollback(name) and Release map to Server savepoints. An autocommit mutation gets a unique request token and runs once. A lost write or commit acknowledgement can raise SdpException.UnknownOutcome. Reconnect as the same user and call GetOutcomeAsync with its RequestToken, CommitWithTokenAsync's original token or the command's LastRequestToken. An unknown ledger status remains unresolved. The provider performs no automatic write replay. Command.Cancel sends a separate authenticated control request scoped to the active Server request ID.

Pooling is opt-in through Pooling=true. Reuse is bounded and restricted to the normalized endpoint, TLS trust, account and credential. On return and checkout the provider uses the Server's session reset. A pooled physical session keeps its authenticated identity during its idle lifetime. Disable pooling when each logical connection must authenticate afresh. Dispose readers, transactions and connections with using or await using.

SdpConnection exposes metadata, capability, model inference, dataset, compute jobs, Decision validation, History and administration methods. Server capabilities and access control determine which operations are available. Results for the management extension retain the exact Server JSON shape.

The embedded and packaged wire contract has SHA-256 e6500900a3fa34910781ea92d5e41fe8716511fac6f6d6c689ff9cce15d578ab. SdpContract exposes the archive and the wire value decoder. Build verification compares the ZIP against its locked digest.

## Local build and test

Run ci.ps1 from the .NET SDK checkout with .NET SDK 10.0.402. It writes out/packages/StarDataParadiseDrive.2.0.3.nupkg and installs that exact version into a fresh local NuGet cache for the .NET 8 and .NET 10 console consumers. Add -Server, -OpenSsl and -FixtureHarness to run the separate real TLS acceptance fixture. The fixture must provision an isolated Server and supply its generated credentials independently of the package.
