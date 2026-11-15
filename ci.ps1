$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot 'out/dotnet'
    $env:DOTNET_NOLOGO = '1'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $PSScriptRoot 'out/nuget'
    New-Item -ItemType Directory -Path out/packages -Force | Out-Null
    & dotnet pack src/StarDataParadise.SDK.NET.csproj -c Release -o out/packages
    if ($LASTEXITCODE) { exit $LASTEXITCODE }
    & dotnet run --project smoke/Smoke.csproj -c Release
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
