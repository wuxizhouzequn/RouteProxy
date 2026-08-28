$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $projectRoot 'tools\dotnet'
$installerPath = Join-Path $env:TEMP 'routeproxy-dotnet-install.ps1'

New-Item -ItemType Directory -Path $installDir -Force | Out-Null
Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath

& $installerPath `
    -Version '10.0.400' `
    -Architecture 'x64' `
    -InstallDir $installDir `
    -NoPath

$dotnet = Join-Path $installDir 'dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'dotnet-install completed without producing dotnet.exe.'
}

& $dotnet --info
