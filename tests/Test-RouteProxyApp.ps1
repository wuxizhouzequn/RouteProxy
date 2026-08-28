param([switch]$CrashRecovery)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$projectRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $projectRoot 'publish\win-x64-v1.4\RouteProxy.exe'
$resultPath = Join-Path $PSScriptRoot 'app-test-result.txt'
Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue
'SCRIPT STARTED' | Set-Content -LiteralPath $resultPath -Encoding utf8

function Find-Control($root, [string]$automationId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Read-Name($root, [string]$automationId) {
    $element = Find-Control $root $automationId
    if ($null -eq $element) { return '<not found>' }
    return $element.Current.Name
}

function Read-Value($root, [string]$automationId) {
    $element = Find-Control $root $automationId
    if ($null -eq $element) { return '<not found>' }
    $pattern = $element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    return $pattern.Current.Value
}

function Read-Enabled($root, [string]$automationId) {
    $element = Find-Control $root $automationId
    if ($null -eq $element) { return $false }
    return $element.Current.IsEnabled
}

function Invoke-Control($root, [string]$automationId) {
    $element = Find-Control $root $automationId
    if ($null -eq $element) { throw "Control not found: $automationId" }
    $pattern = $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Read-ProxyState {
    $path = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $key = Get-Item -Path $path
    $values = Get-ItemProperty -Path $path
    $names = @($key.GetValueNames())
    return [ordered]@{
        ProxyEnableExists = $names -contains 'ProxyEnable'
        ProxyEnable = $values.ProxyEnable
        ProxyServerExists = $names -contains 'ProxyServer'
        ProxyServer = $values.ProxyServer
        AutoConfigUrlExists = $names -contains 'AutoConfigURL'
        AutoConfigUrl = $values.AutoConfigURL
        AutoDetectExists = $names -contains 'AutoDetect'
        AutoDetect = $values.AutoDetect
    } | ConvertTo-Json -Compress
}

$app = $null
try {
    $proxyBefore = Read-ProxyState
    $app = Start-Process -FilePath $exePath -PassThru
    'APP PROCESS STARTED' | Add-Content -LiteralPath $resultPath -Encoding utf8
    for ($attempt = 0; $attempt -lt 60 -and $app.MainWindowHandle -eq 0; $attempt++) {
        Start-Sleep -Milliseconds 100
        $app.Refresh()
    }
    if ($app.MainWindowHandle -eq 0) { throw 'RouteProxy main window did not appear.' }
    $root = [Windows.Automation.AutomationElement]::FromHandle($app.MainWindowHandle)
    Invoke-Control $root 'StartButton'

    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 500
        if ((Read-Enabled $root 'StopButton') -and $attempt -ge 10) { break }
        if ((Read-Enabled $root 'StartButton') -and $attempt -ge 4) { break }
    }

    $pacUrl = '<not run>'
    $pacRule = '<not run>'
    $chatGptProxy = '<not run>'
    $normalProxy = '<not run>'
    $domainHttp = '<not run>'
    if (Read-Enabled $root 'StopButton') {
        $pacUrl = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings').AutoConfigURL
        try {
            $pacContent = (Invoke-WebRequest -UseBasicParsing -Uri $pacUrl -TimeoutSec 5).Content
            if ($pacContent -is [byte[]]) { $pacContent = [Text.Encoding]::UTF8.GetString($pacContent) }
            $pacRule = if ($pacContent -match 'chatgpt\.com' -and $pacContent -match 'PROXY 127\.0\.0\.1:\d+') { 'OK' } else { 'FAILED' }
            $systemProxy = [System.Net.WebRequest]::GetSystemWebProxy()
            $chatGptProxy = $systemProxy.GetProxy([Uri]'https://chatgpt.com').AbsoluteUri
            $normalProxy = $systemProxy.GetProxy([Uri]'https://example.com').AbsoluteUri
        }
        catch {
            $pacRule = 'FAILED: ' + $_.Exception.Message
        }
        try {
            $domainHttp = (Invoke-WebRequest -UseBasicParsing -Uri 'https://chatgpt.com' -TimeoutSec 20).StatusCode
        }
        catch {
            $domainHttp = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 'FAILED: ' + $_.Exception.Message }
        }
    }

    # Wait until NormalIpText settles (placeholder ends with U+2026 ellipsis).
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $ipText = Read-Name $root 'NormalIpText'
        if ($ipText -and -not $ipText.EndsWith([char]0x2026)) { break }
        Start-Sleep -Milliseconds 500
    }

    $runningStatus = Read-Name $root 'StatusText'
    $normalIp = Read-Name $root 'NormalIpText'
    $staticIp = Read-Name $root 'StaticIpText'
    $runningLog = Read-Value $root 'LogBox'

    $crashResult = '<not run>'
    if ($CrashRecovery) {
        Stop-Process -Id $app.Id -Force
        $app.WaitForExit(5000) | Out-Null
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            Start-Sleep -Milliseconds 250
            $backupExists = Test-Path (Join-Path $env:LOCALAPPDATA 'RouteProxy\system-proxy-backup.json')
            $ownCore = Get-CimInstance Win32_Process -Filter "Name = 'sing-box.exe'" -ErrorAction SilentlyContinue |
                Where-Object CommandLine -Like '*\AppData\Local\RouteProxy\runtime-*.json*'
            if (-not $backupExists -and -not $ownCore -and (Read-ProxyState) -eq $proxyBefore) { break }
        }
        $crashResult = "BackupExists=$backupExists; CoreRunning=$([bool]$ownCore); ProxyRestored=$((Read-ProxyState) -eq $proxyBefore)"
        $stoppedStatus = '<process killed>'
        $finalLog = $runningLog
    }
    else {
        if (Read-Enabled $root 'StopButton') {
            Invoke-Control $root 'StopButton'
            for ($attempt = 0; $attempt -lt 40 -and -not (Read-Enabled $root 'StartButton'); $attempt++) {
                Start-Sleep -Milliseconds 250
            }
        }
        $stoppedStatus = Read-Name $root 'StatusText'
        $finalLog = Read-Value $root 'LogBox'
    }

    $proxyRestored = (Read-ProxyState) -eq $proxyBefore
    @(
        "RunningStatus: $runningStatus"
        "NormalExitIp: $normalIp"
        "StaticExitIp: $staticIp"
        "PacUrl: $pacUrl"
        "PacRule: $pacRule"
        "ChatGptProxy: $chatGptProxy"
        "NormalProxy: $normalProxy"
        "ChatGptHttp: $domainHttp"
        "ProxyRestored: $proxyRestored"
        "CrashRecovery: $crashResult"
        "StoppedStatus: $stoppedStatus"
        '--- Running log ---'
        $runningLog
        '--- Final log ---'
        $finalLog
    ) | Set-Content -LiteralPath $resultPath -Encoding utf8
}
catch {
    ("FAILED: " + $_.Exception.Message) | Set-Content -LiteralPath $resultPath -Encoding utf8
    throw
}
finally {
    if ($null -ne $app -and -not $app.HasExited) {
        $app.CloseMainWindow() | Out-Null
        if (-not $app.WaitForExit(12000)) {
            Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue
        }
    }
}
