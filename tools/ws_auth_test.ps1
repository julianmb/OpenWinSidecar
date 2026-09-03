param([string]$Password = "test123")

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ct = [System.Threading.CancellationToken]::None
$ws.ConnectAsync([Uri]"ws://localhost:28252/", $ct).Wait()

$buf = New-Object byte[] (4194304)
$sw = [System.Diagnostics.Stopwatch]::StartNew()

function Receive-Message {
    param($ws, $ct, $buf)
    $ms = New-Object System.IO.MemoryStream
    $r = $null
    do {
        $seg = [ArraySegment[byte]]::new($buf)
        $r = $ws.ReceiveAsync($seg, $ct).Result
        if ($r.Count -gt 0) { $ms.Write($buf, 0, $r.Count) }
    } while (-not $r.EndOfMessage)
    if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Text) {
        return @{ kind = "text"; data = [System.Text.Encoding]::UTF8.GetString($ms.ToArray()) }
    }
    return @{ kind = "binary"; size = $ms.Length }
}

# 1. Expect auth:required
$m1 = Receive-Message $ws $ct $buf
Write-Output ("STEP1 (expect auth:required): {0} '{1}'" -f $m1.kind, $(if ($m1.kind -eq 'text') { $m1.data } else { $m1.size }))

# 2. Send wrong password
$wrong = [System.Text.Encoding]::UTF8.GetBytes("auth:WRONG-$Password")
$ws.SendAsync([ArraySegment[byte]]::new($wrong), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).Wait()
$m2 = Receive-Message $ws $ct $buf
Write-Output ("STEP2 (expect auth:denied):   {0} '{1}'" -f $m2.kind, $(if ($m2.kind -eq 'text') { $m2.data } else { $m2.size }))

# 3. Send correct password
$right = [System.Text.Encoding]::UTF8.GetBytes("auth:$Password")
$ws.SendAsync([ArraySegment[byte]]::new($right), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).Wait()
$m3 = Receive-Message $ws $ct $buf
Write-Output ("STEP3 (expect auth:ok):       {0} '{1}'" -f $m3.kind, $(if ($m3.kind -eq 'text') { $m3.data } else { $m3.size }))

# 4. Count frames received in 10 seconds (idle-skip check: static desktop => few frames)
$frames = 0
$pings = 0
$measureStart = $sw.ElapsedMilliseconds
while ($sw.ElapsedMilliseconds -lt 14000) {
    $left = 14000 - $sw.ElapsedMilliseconds
    $task = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), $ct)
    if (-not $task.Wait([Math]::Max($left, 1))) { break }
    $r = $task.Result
    if ($r.Count -gt 0) {
        if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Binary) { $frames++ }
        elseif ($buf[0] -eq 0x89) { $pings++ }
    }
}
$ws.Abort()
$measureSec = ($sw.ElapsedMilliseconds - $measureStart) / 1000.0
Write-Output ("STEP4 ({0:F1}s window):  frames={1} rate={2:F0}/s pings={3}" -f $measureSec, $frames, ($frames / $measureSec), $pings)

# 5. HTTP /input guard
try {
    $noPw = Invoke-WebRequest -Uri "http://localhost:28252/input?action=down&display=2&x=0.5&y=0.5" -UseBasicParsing -TimeoutSec 3
    Write-Output ("STEP5a /input no pw:          HTTP {0}" -f $noPw.StatusCode)
} catch {
    Write-Output ("STEP5a /input no pw:          HTTP {0}" -f $_.Exception.Response.StatusCode.value__)
}
try {
    $withPw = Invoke-WebRequest -Uri "http://localhost:28252/input?action=down&display=2&x=0.5&y=0.5&pw=$Password" -UseBasicParsing -TimeoutSec 3
    Write-Output ("STEP5b /input with pw:        HTTP {0}" -f $withPw.StatusCode)
} catch {
    Write-Output ("STEP5b /input with pw:        HTTP {0}" -f $_.Exception.Response.StatusCode.value__)
}
