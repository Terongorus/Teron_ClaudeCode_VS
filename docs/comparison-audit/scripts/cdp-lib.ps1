# Minimal Chrome DevTools Protocol client over System.Net.WebSockets.ClientWebSocket.
# Dot-source this file, then use Connect-Cdp / Send-CdpCommand / Close-Cdp.

Add-Type -AssemblyName System.Net.WebSockets -ErrorAction SilentlyContinue

function Connect-Cdp {
    param([Parameter(Mandatory=$true)][string]$WsUrl)
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $uri = New-Object System.Uri($WsUrl)
    $cts = New-Object System.Threading.CancellationTokenSource
    $cts.CancelAfter(10000)
    # IMPORTANT: .GetAwaiter().GetResult() on a void-returning Task can leak a stray
    # VoidTaskResult object into this function's output stream in Windows PowerShell 5.1 if not
    # suppressed - that would silently turn the caller's "$ws = Connect-Cdp ..." into a 2-element
    # array [VoidTaskResult, ClientWebSocket], breaking every later $ws.Method(...) call. Always
    # pipe void-returning GetResult() calls to Out-Null.
    $ws.ConnectAsync($uri, $cts.Token).GetAwaiter().GetResult() | Out-Null
    return $ws
}

$global:CdpMsgId = 0

function Send-CdpCommand {
    param(
        [Parameter(Mandatory=$true)]$Ws,
        [Parameter(Mandatory=$true)][string]$Method,
        [hashtable]$Params = @{},
        [int]$TimeoutMs = 10000
    )
    $global:CdpMsgId++
    $id = $global:CdpMsgId
    $payload = @{ id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 20 -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($payload)
    $segment = New-Object System.ArraySegment[byte] (,$bytes)
    $cts = New-Object System.Threading.CancellationTokenSource
    $cts.CancelAfter($TimeoutMs)
    $Ws.SendAsync($segment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).GetAwaiter().GetResult() | Out-Null

    # Read messages until we see the response with matching id (skip async events).
    $deadline = (Get-Date).AddMilliseconds($TimeoutMs)
    while ((Get-Date) -lt $deadline) {
        $buffer = New-Object byte[] 65536
        $recvSegment = New-Object System.ArraySegment[byte] (,$buffer)
        $rcts = New-Object System.Threading.CancellationTokenSource
        $rcts.CancelAfter($TimeoutMs)
        $sb = New-Object System.Text.StringBuilder
        do {
            $result = $Ws.ReceiveAsync($recvSegment, $rcts.Token).GetAwaiter().GetResult()
            $sb.Append([System.Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count)) | Out-Null
        } while (-not $result.EndOfMessage)
        $text = $sb.ToString()
        try { $obj = $text | ConvertFrom-Json } catch { continue }
        if ($obj.id -eq $id) { return $obj }
        # else: it's an event notification, keep waiting for our response
    }
    throw "Timed out waiting for CDP response to $Method (id=$id)"
}

function Close-Cdp {
    param([Parameter(Mandatory=$true)]$Ws)
    try {
        $cts = New-Object System.Threading.CancellationTokenSource
        $cts.CancelAfter(3000)
        $Ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", $cts.Token).GetAwaiter().GetResult() | Out-Null
    } catch {}
}

function Invoke-CdpEval {
    param([Parameter(Mandatory=$true)]$Ws, [Parameter(Mandatory=$true)][string]$Expression)
    $resp = Send-CdpCommand -Ws $Ws -Method "Runtime.evaluate" -Params @{ expression = $Expression; returnByValue = $true }
    return $resp.result.result.value
}

function Get-ClaudeCodeWebviewContext {
    # Discovers the real Claude Code chat webview inside an isolated VS Code instance and
    # returns @{ Ws = <connected ClientWebSocket>; ContextId = <isolated-world executionContextId> }
    # ready for Invoke-CdpEvalInContext. VS Code webviews are double-nested: an outer wrapper frame
    # (VS Code's own preload/sandbox script - NOT the extension's content) contains an inner
    # "active-frame" child frame with the extension's real Preact app. Reaching it requires
    # Page.createIsolatedWorld scoped to that child frame's frameId, then Runtime.evaluate with
    # the resulting contextId - plain Runtime.evaluate on the outer frame only sees the wrapper.
    param([int]$CdpPort = 9333, [int]$TimeoutMs = 15000)
    $targets = Invoke-RestMethod -Uri "http://localhost:$CdpPort/json"
    $t = $targets | Where-Object { $_.type -eq "iframe" -and $_.url -like "*extensionId=Anthropic.claude-code*" } | Select-Object -First 1
    if (-not $t) { throw "No Claude Code webview target found on CDP port $CdpPort" }
    $ws = Connect-Cdp -WsUrl $t.webSocketDebuggerUrl
    Send-CdpCommand -Ws $ws -Method "Page.enable" -TimeoutMs $TimeoutMs | Out-Null
    Send-CdpCommand -Ws $ws -Method "Runtime.enable" -TimeoutMs $TimeoutMs | Out-Null
    $ft = Send-CdpCommand -Ws $ws -Method "Page.getFrameTree" -TimeoutMs $TimeoutMs
    $activeFrame = $ft.result.frameTree.childFrames | Where-Object { $_.frame.name -eq "active-frame" } | Select-Object -First 1
    if (-not $activeFrame) { throw "No active-frame child found under the Claude Code webview wrapper" }
    $iso = Send-CdpCommand -Ws $ws -Method "Page.createIsolatedWorld" -Params @{
        frameId = $activeFrame.frame.id; worldName = "automation"; grantUniversalAccess = $true
    } -TimeoutMs $TimeoutMs
    return @{ Ws = $ws; ContextId = $iso.result.executionContextId }
}

function Invoke-CdpEvalInContext {
    param([Parameter(Mandatory=$true)]$Ctx, [Parameter(Mandatory=$true)][string]$Expression, [int]$TimeoutMs = 10000)
    $resp = Send-CdpCommand -Ws $Ctx.Ws -Method "Runtime.evaluate" -Params @{
        expression = $Expression; contextId = $Ctx.ContextId; returnByValue = $true
    } -TimeoutMs $TimeoutMs
    if ($resp.result.exceptionDetails) {
        throw "JS exception: $($resp.result.exceptionDetails.exception.description)"
    }
    return $resp.result.result.value
}

function Get-CdpScreenshot {
    param([Parameter(Mandatory=$true)]$Ws, [Parameter(Mandatory=$true)][string]$OutFile)
    $resp = Send-CdpCommand -Ws $Ws -Method "Page.captureScreenshot" -Params @{ format = "png" } -TimeoutMs 15000
    $b64 = $resp.result.data
    [System.IO.File]::WriteAllBytes($OutFile, [System.Convert]::FromBase64String($b64))
    return $OutFile
}
