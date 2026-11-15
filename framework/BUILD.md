# StarDataParadiseNetFramework

2.0.0-preview.1 is the ADO.NET provider for StarDataParadise Server 0.3.0 and later servers speaking wire v1. The package targets .NET Framework 4.8 and uses TLS 1.2. Visual Studio 2019 consumers can target .NET Framework 4.8 with its targeting pack.

The 2020 package 1.0.6 exposed the legacy node API and targeted .NET Standard 2.0. Applications that use those types can keep their explicit 1.0.6 reference. The 2.0 preview uses `StarDataParadise.SDK.SdpConnection`, SQL parameters, transactions and named procedure result sets. Connection and repository settings are supplied by the application.

~~~xml
<PackageReference Include="StarDataParadiseNetFramework" Version="2.0.0-preview.1" />
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
