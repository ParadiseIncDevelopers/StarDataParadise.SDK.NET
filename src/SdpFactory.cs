using System;
using System.Data.Common;
using System.IO;
using System.Text.Json;

namespace StarDataParadise.SDK;

public sealed class SdpFactory : DbProviderFactory
{
    public const string InvariantName = "StarDataParadise.SDK.NET";
    public static readonly SdpFactory Instance = new();
    private SdpFactory() { }

    public override DbConnection CreateConnection() => new SdpConnection();
    public override DbCommand CreateCommand() => new SdpCommand();
    public override DbParameter CreateParameter() => new SdpParameter();
    public override DbConnectionStringBuilder CreateConnectionStringBuilder() => new();

#if NETFRAMEWORK
    public static void Register() =>
        throw new NotSupportedException("Use SdpFactory.Instance or register the factory in app.config on .NET Framework.");
#else
    public static void Register() => DbProviderFactories.RegisterFactory(InvariantName, Instance);
#endif
}

public static class SdpContract
{
    public const int WireVersion = 1;
    public const string ArchiveSha256 = "e6500900a3fa34910781ea92d5e41fe8716511fac6f6d6c689ff9cce15d578ab";

    public static Stream OpenArchive() =>
        typeof(SdpContract).Assembly.GetManifestResourceStream("StarDataParadise.SDK.WireContract.v1.zip") ??
        throw new InvalidOperationException("The pinned wire contract is missing.");

    public static object DecodeValue(JsonElement value) => SdpValues.Decode(value);
}