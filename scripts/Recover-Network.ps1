param(
    [int]$WaitForProcessId = 0,
    [switch]$Silent
)

$ErrorActionPreference = 'Continue'

$routeProxyData = Join-Path $env:LOCALAPPDATA 'RouteProxy'
$proxyBackupPath = Join-Path $routeProxyData 'system-proxy-backup.json'
$internetSettings = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'

function Restore-RegistryValue($data, [string]$existsName, [string]$valueName, [string]$registryName, [string]$type) {
    if ($data.$existsName) {
        Set-ItemProperty -Path $internetSettings -Name $registryName -Value $data.$valueName -Type $type -ErrorAction Stop
    } else {
        Remove-ItemProperty -Path $internetSettings -Name $registryName -ErrorAction SilentlyContinue
    }
}

function Restore-SystemProxy {
    if (-not (Test-Path -LiteralPath $proxyBackupPath)) { return }
    try {
        $data = Get-Content -LiteralPath $proxyBackupPath -Raw | ConvertFrom-Json
        Restore-RegistryValue $data 'ProxyEnableExists' 'ProxyEnable' 'ProxyEnable' 'DWord'
        Restore-RegistryValue $data 'ProxyServerExists' 'ProxyServer' 'ProxyServer' 'String'
        Restore-RegistryValue $data 'AutoConfigUrlExists' 'AutoConfigUrl' 'AutoConfigURL' 'String'
        Restore-RegistryValue $data 'AutoDetectExists' 'AutoDetect' 'AutoDetect' 'DWord'
        Remove-Item -LiteralPath $proxyBackupPath -Force -ErrorAction SilentlyContinue
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RouteProxyWinInet {
    [DllImport("wininet.dll", SetLastError=true)]
    public static extern bool InternetSetOption(IntPtr h, int option, IntPtr buffer, int length);
}
'@ -ErrorAction SilentlyContinue
        [RouteProxyWinInet]::InternetSetOption([IntPtr]::Zero, 39, [IntPtr]::Zero, 0) | Out-Null
        [RouteProxyWinInet]::InternetSetOption([IntPtr]::Zero, 37, [IntPtr]::Zero, 0) | Out-Null
        Write-Host 'Original Windows proxy settings were restored.' -ForegroundColor Green
    } catch {
        Write-Host ('Failed to restore Windows proxy settings: ' + $_.Exception.Message) -ForegroundColor Red
    }
}

if ($WaitForProcessId -gt 0) {
    Wait-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
} else {
    Write-Host 'RouteProxy emergency network recovery' -ForegroundColor Cyan
    Write-Host 'Only RouteProxy temporary state will be cleaned.'
    Get-Process -Name RouteProxy -ErrorAction SilentlyContinue | Stop-Process -Force
}

Restore-SystemProxy

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$legacyNrpt = Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object { $_.Comment -like 'RouteProxy:*' }
$legacyRoutes = Get-NetRoute -InterfaceAlias 'RouteProxy' -ErrorAction SilentlyContinue
$legacyAdapter = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'RouteProxy' -and $_.Status -eq 'Up' }
$needsLegacyCleanup = $legacyNrpt -or $legacyRoutes -or $legacyAdapter
if ($needsLegacyCleanup -and -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath))
    if ($Silent) { $arguments += '-Silent' }
    Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments
    exit
}

Get-DnsClientNrptRule -ErrorAction SilentlyContinue |
    Where-Object { $_.Comment -like 'RouteProxy:*' } |
    Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue

$routeProxyCore = Get-CimInstance Win32_Process -Filter "Name = 'sing-box.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*\AppData\Local\RouteProxy\runtime-*.json*' }
foreach ($core in $routeProxyCore) {
    Stop-Process -Id $core.ProcessId -Force -ErrorAction SilentlyContinue
}

Start-Sleep -Milliseconds 800
$routeProxyRoutes = Get-NetRoute -InterfaceAlias 'RouteProxy' -ErrorAction SilentlyContinue
if ($routeProxyRoutes) {
    $routeProxyRoutes | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue
}
$adapter = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue |
    Where-Object Name -eq 'RouteProxy'
if ($adapter) {
    $adapter | Disable-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host 'Residual RouteProxy adapter was disabled.' -ForegroundColor Yellow
} else {
    Write-Host 'No residual RouteProxy adapter was found.' -ForegroundColor Green
}

ipconfig.exe /flushdns | Out-Null

$stillRunning = Get-CimInstance Win32_Process -Filter "Name = 'sing-box.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*\AppData\Local\RouteProxy\runtime-*.json*' }
$stillEnabled = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'RouteProxy' -and $_.Status -eq 'Up' }

if (-not $stillRunning -and -not $stillEnabled) {
    Write-Host 'Recovery completed. RouteProxy routes are no longer active.' -ForegroundColor Green
} else {
    Write-Host 'Recovery is incomplete. Restart Windows before using RouteProxy again.' -ForegroundColor Red
}

if (-not $Silent) {
    Write-Host 'Your VPN and Windows system proxy settings were not changed.'
    Read-Host 'Press Enter to close'
}
