param([string]$Password = "564D7EC4")

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ct = [System.Threading.CancellationToken]::None
$ws.ConnectAsync([Uri]"ws://localhost:28252/", $ct).Wait()

$buf = New-Object byte[] (4194304)
function Receive-FullMessage {
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
    return @{ kind = "binary"; data = $ms.ToArray() }
}

# authenticate
$m = Receive-FullMessage $ws $ct $buf
Write-Output ("auth: " + $m.data)
$pw = [System.Text.Encoding]::UTF8.GetBytes("auth:$Password")
$ws.SendAsync([ArraySegment[byte]]::new($pw), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).Wait()
$m = Receive-FullMessage $ws $ct $buf
Write-Output ("auth result: " + $m.data)

# measure 8s of properly-assembled frames
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$counts = @{}
$seenTs = @{}
$duplicateTs = 0
$sizeSum = 0
$measureMs = 8000
while ($sw.ElapsedMilliseconds -lt $measureMs) {
    $left = $measureMs - $sw.ElapsedMilliseconds
    $task = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), $ct)
    if (-not $task.Wait([Math]::Max($left, 1))) { break }
    $r = $task.Result
    if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { break }
    if ($r.MessageType -ne [System.Net.WebSockets.WebSocketMessageType]::Binary) { continue }

    $ms = New-Object System.IO.MemoryStream
    do {
        if ($r.Count -gt 0) { $ms.Write($buf, 0, $r.Count) }
        if ($r.EndOfMessage) { break }
        $seg2 = [ArraySegment[byte]]::new($buf)
        $r = $ws.ReceiveAsync($seg2, $ct).Result
    } while ($true)
    $data = $ms.ToArray()
    if ($data.Length -lt 15) { continue }

    $codec = $data[0]
    $ts = 0
    for ($i = 2; $i -le 9; $i++) { $ts = $ts * 256 + $data[$i] }

    $counts[$codec] = 1 + $(if ($counts.ContainsKey($codec)) { $counts[$codec] } else { 0 })
    $sizeSum += $data.Length
    if ($seenTs.ContainsKey($ts)) { $duplicateTs++ } else { $seenTs[$ts] = 1 }
}
$ws.Abort()

$total = 0; $counts.Values | ForEach-Object { $total += $_ }
$codecStr = ($counts.GetEnumerator() | ForEach-Object { "codec$($_.Key)=$($_.Value)" }) -join " "
$mbps = [math]::Round(($sizeSum * 8) / ($measureMs / 1000.0) / 1000000.0, 2)
Write-Output ("totalFrames={0} ({1:F0}/s) distinctTimestamps={2} duplicateTimestamps={3} bandwidth={4} Mbps" -f $total, ($total / ($measureMs / 1000.0)), $seenTs.Count, $duplicateTs, $mbps)
Write-Output ("codec counts: {0}" -f $codecStr)
