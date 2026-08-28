$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot 'tools\dotnet\dotnet.exe'
$project = Join-Path $projectRoot 'src\RouteProxy\RouteProxy.csproj'
$output = Join-Path $projectRoot 'publish\win-x64-v1.4'

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'Project-local .NET SDK is missing. Run scripts\install-dotnet.ps1 first.'
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}

& $dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $output `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Published to $output"
