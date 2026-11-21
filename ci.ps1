param(
    [string]$Server,
    [string]$OpenSsl,
    [string]$FixtureHarness
)
$ErrorActionPreference = 'Stop'
$integration = @($Server, $OpenSsl, $FixtureHarness) | Where-Object { $_ }
if (@($integration).Count -ne 0 -and @($integration).Count -ne 3) {
    throw 'Supply Server, OpenSsl and FixtureHarness together.'
}
if (@($integration).Count -eq 3) {
    $Server = (Resolve-Path -LiteralPath $Server).Path
    $OpenSsl = (Resolve-Path -LiteralPath $OpenSsl).Path
    $FixtureHarness = (Resolve-Path -LiteralPath $FixtureHarness).Path
}
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'out/dotnet'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot ('out/nuget/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path out/packages -Force | Out-Null
    & dotnet pack src/StarDataParadise.SDK.NET.csproj -c Release -o out/packages
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    & dotnet pack framework/StarDataParadiseNetFramework.csproj -c Release -o out/packages
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    & dotnet build smoke/Smoke.csproj -c Release --force
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    foreach ($target in 'net8.0', 'net10.0') {
        $consumer = (Resolve-Path -LiteralPath "smoke/bin/Release/$target/Smoke.dll").Path
        if ($FixtureHarness) {
            $contract = (Resolve-Path -LiteralPath 'contracts/sdp-contract-v1.zip').Path
            & python -B $FixtureHarness --server $Server --openssl $OpenSsl --contract $contract --scratch "out/integration/$target" --drop-op tx_commit -- dotnet $consumer
        }
        else {
            & dotnet $consumer
        }
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
    & dotnet build native-smoke/NativeSmoke.csproj -c Release --force -p:InstalledPackageFixture=true
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    foreach ($target in 'net8.0', 'net10.0') {
        & dotnet "native-smoke/bin/Release/$target/NativeSmoke.dll"
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
    }
    & dotnet build smoke/Smoke.csproj -c Release --force -p:FrameworkFixture=true
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    & 'smoke/bin/Release/net48/Smoke.exe'
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    & dotnet build native-smoke/NativeSmoke.csproj -c Release --force -p:FrameworkFixture=true -p:InstalledPackageFixture=true
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    & 'native-smoke/bin/Release/net48/NativeSmoke.exe'
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    exit 0
}
finally {
    Pop-Location
}