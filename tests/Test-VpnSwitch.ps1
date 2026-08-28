# Real VPN-switch acceptance test (HANDOFF section 7)
# Flow: start routing -> wait for user to switch VPN node/mode -> detect change ->
# verify auto-rebuild and exits -> stop and restore.
param([int]$WaitMinutes = 6)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$projectRoot = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $projectRoot 'publish\win-x64-v1.4\RouteProxy.exe'
$resultPath = Join-Path $PSScriptRoot 'vpn-switch-result.txt'
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
    $values = Get-ItemProperty -Path $path
    return "$($values.ProxyEnable)|$($values.ProxyServer)"
}
function Get-AdapterSignature {
    return (Get-NetAdapter | Where-Object Status -eq 'Up' |
        ForEach-Object { "$($_.Name)=$($_.ifIndex)" }) -join ','
}
function Get-UpstreamExitIp([string]$proxyAddress) {
    try {
        $proxy = [System.Net.WebProxy]::new("http://$proxyAddress")
        $request = [System.Net.HttpWebRequest]::Create('https://api.ipify.org')
        $request.Proxy = $proxy
        $request.Timeout = 6000
        $response = $request.GetResponse()
        $reader = [IO.StreamReader]::new($response.GetResponseStream())
        $value = $reader.ReadToEnd().Trim()
        $reader.Close(); $response.Close()
        $parsed = $null
        if ([System.Net.IPAddress]::TryParse($value, [ref]$parsed)) { return $value }
    } catch { }
    return $null
}

$app = $null
try {
    $proxyBefore = Read-ProxyState
    $adaptersBefore = Get-AdapterSignature
    $app = Start-Process -FilePath $exePath -PassThru
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
    if (-not (Read-Enabled $root 'StopButton')) { throw 'Start failed; StopButton not enabled.' }

    # Wait until NormalIpText settles (placeholder ends with U+2026 ellipsis).
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $ipText = Read-Name $root 'NormalIpText'
        if ($ipText -and -not $ipText.EndsWith([char]0x2026)) { break }
        Start-Sleep -Milliseconds 500
    }

    $normalBefore = Read-Name $root 'NormalIpText'
    $staticBefore = Read-Name $root 'StaticIpText'
    $registry = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
    $upstreamAddress = $registry.ProxyServer
    $pacUrlBefore = $registry.AutoConfigURL
    "BEFORE normal=$normalBefore static=$staticBefore upstream=$upstreamAddress pac=$pacUrlBefore adapters=$adaptersBefore" |
        Add-Content -LiteralPath $resultPath -Encoding utf8

    'WAITING: 30s grace period, then polling for VPN switch...' | Add-Content -LiteralPath $resultPath -Encoding utf8
    Start-Sleep -Seconds 30

    $switchDetected = ''
    $deadline = (Get-Date).AddMinutes($WaitMinutes)
    while ((Get-Date) -lt $deadline -and -not $switchDetected) {
        $proxyNow = Read-ProxyState
        $adaptersNow = Get-AdapterSignature
        $exitNow = $null
        if ($upstreamAddress -match '^[0-9.]+:\d+$') {
            $exitNow = Get-UpstreamExitIp $upstreamAddress
        }
        if ($adaptersNow -ne $adaptersBefore) {
            $switchDetected = "adapter-change ($adaptersBefore -> $adaptersNow)"
        } elseif ($proxyNow -ne $proxyBefore) {
            $switchDetected = "system-proxy-change ($proxyBefore -> $proxyNow)"
        } elseif ($exitNow -and $normalBefore -match '^\d+\.\d+\.\d+\.\d+$' -and $exitNow -ne $normalBefore) {
            $switchDetected = "exit-ip-change ($normalBefore -> $exitNow)"
        }
        if (-not $switchDetected) { Start-Sleep -Seconds 10 }
    }

    if (-not $switchDetected) {
        "RESULT: TIMEOUT - no VPN switch detected within $WaitMinutes minutes" | Add-Content -LiteralPath $resultPath -Encoding utf8
    } else {
        "SWITCH DETECTED: $switchDetected at $(Get-Date -Format 'HH:mm:ss')" | Add-Content -LiteralPath $resultPath -Encoding utf8
        # Wait for auto-rebuild and network to settle.
        Start-Sleep -Seconds 20

        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            $ipText2 = Read-Name $root 'NormalIpText'
            if ($ipText2 -and -not $ipText2.EndsWith([char]0x2026)) { break }
            Start-Sleep -Milliseconds 500
        }
        $normalAfter = Read-Name $root 'NormalIpText'
        $staticAfter = Read-Name $root 'StaticIpText'
        $runningStatus = Read-Name $root 'StatusText'
        $log = Read-Value $root 'LogBox'
        $pacUrlAfter = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings').AutoConfigURL

        $pacOk = '<not run>'
        $chatGptProxy = '<not run>'
        try {
            $pacContent = (Invoke-WebRequest -UseBasicParsing -Uri $pacUrlAfter -TimeoutSec 5).Content
            if ($pacContent -is [byte[]]) { $pacContent = [Text.Encoding]::UTF8.GetString($pacContent) }
            $pacOk = if ($pacContent -match 'chatgpt\.com' -and $pacContent -match 'PROXY 127\.0\.0\.1:\d+') { 'OK' } else { 'FAILED' }
            $systemProxy = [System.Net.WebRequest]::GetSystemWebProxy()
            $chatGptProxy = $systemProxy.GetProxy([Uri]'https://chatgpt.com').AbsoluteUri
        } catch {
            $pacOk = 'FAILED: ' + $_.Exception.Message
        }
        $rebuildLogged = ($log -match 'Rebuild|rebuild|network change|VPN')

        "AFTER normal=$normalAfter static=$staticAfter status=$runningStatus" | Add-Content -LiteralPath $resultPath -Encoding utf8
        "PAC after: $pacUrlAfter rule=$pacOk chatgpt-proxy=$chatGptProxy rebuild-logged=$rebuildLogged" | Add-Content -LiteralPath $resultPath -Encoding utf8

        $normalIsIp = $normalAfter -match '^\d+\.\d+\.\d+\.\d+$'
        $staticIsIp = $staticAfter -match '^\d+\.\d+\.\d+\.\d+$'
        $verdicts = @()
        $verdicts += "normal-changed=" + ($normalIsIp -and ($normalAfter -ne $normalBefore))
        $verdicts += "static-kept=" + ($staticIsIp -and ($staticAfter -eq $staticBefore))
        $verdicts += "pac-ok=" + ($pacOk -eq 'OK')
        $verdicts += "pac-hit=" + ($chatGptProxy -match '^http://127\.0\.0\.1:\d+')
        "VERDICT: $($verdicts -join ' ')" | Add-Content -LiteralPath $resultPath -Encoding utf8
        '--- GUI log ---' | Add-Content -LiteralPath $resultPath -Encoding utf8
        $log | Add-Content -LiteralPath $resultPath -Encoding utf8
    }

    # Stop and restore.
    if (Read-Enabled $root 'StopButton') {
        Invoke-Control $root 'StopButton'
        for ($attempt = 0; $attempt -lt 40 -and -not (Read-Enabled $root 'StartButton'); $attempt++) {
            Start-Sleep -Milliseconds 250
        }
    }
    $proxyRestored = (Read-ProxyState) -eq $proxyBefore
    "CLOSED proxy-restored=$proxyRestored" | Add-Content -LiteralPath $resultPath -Encoding utf8
}
catch {
    ("FAILED: " + $_.Exception.Message) | Add-Content -LiteralPath $resultPath -Encoding utf8
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
