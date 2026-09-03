param([int]$DurationMs = 4000, [string]$Label = "Client1")

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ct = [System.Threading.CancellationToken]::None
$ws.ConnectAsync([Uri]"ws://localhost:28252/", $ct).Wait()

# Pin the JPEG intra path for deterministic label checks
$codecMsg = [System.Text.Encoding]::UTF8.GetBytes("codec:intra")
$ws.SendAsync([ArraySegment[byte]]::new($codecMsg), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).Wait()

$buf = New-Object byte[] (4194304)
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$frames = 0
$totalBytes = 0
$codecCounts = @{}
$lastTs = -1
$tsViolations = 0
$sizeSamples = @()
$cursorVisibleCount = 0

while ($sw.ElapsedMilliseconds -lt $DurationMs) {
    $ms = New-Object System.IO.MemoryStream
    $r = $null
    do {
        $seg = [ArraySegment[byte]]::new($buf)
        $r = $ws.ReceiveAsync($seg, $ct).Result
        if ($r.Count -gt 0) { $ms.Write($buf, 0, $r.Count) }
    } while (-not $r.EndOfMessage)

    if ($r.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { break }
    if ($r.MessageType -ne [System.Net.WebSockets.WebSocketMessageType]::Binary) { continue }

    $data = $ms.ToArray()
    if ($data.Length -lt 15) { continue }

    $frames++
    $totalBytes += $data.Length
    $codec = $data[0]
    $flags = $data[1]
    if (($flags -band 0x02) -ne 0) { $cursorVisibleCount++ }

    if (-not $codecCounts.ContainsKey($codec)) { $codecCounts[$codec] = 0 }
    $codecCounts[$codec]++

    if ($sizeSamples.Count -lt 3) { $sizeSamples += $data.Length }

    $ts = 0
    for ($i = 2; $i -le 9; $i++) { $ts = $ts * 256 + $data[$i] }
    if ($ts -le $lastTs) { $tsViolations++ }
    $lastTs = $ts

    if (($frames % 120) -eq 0) { Write-Output ("[{0}] progress: frames={1} elapsed={2}ms" -f $Label, $frames, $sw.ElapsedMilliseconds) }
}

$ws.Abort()

$elapsedSec = $DurationMs / 1000.0
$codecStr = ($codecCounts.GetEnumerator() | ForEach-Object { "codec$($_.Key)=$($_.Value)" }) -join " "
$avgKb = [math]::Round(($totalBytes / [math]::Max($frames,1)) / 1024.0, 1)
Write-Output ("[{0}] frames={1} fps={2} avgFrameKB={3} totalMB={4} {5} tsViolations={6} cursorVisibleFrames={7} firstSizes={8}" -f `
    $Label, $frames, [math]::Round($frames / $elapsedSec, 1), $avgKb, [math]::Round($totalBytes / 1MB, 2), `
    $codecStr, $tsViolations, $cursorVisibleCount, ($sizeSamples -join ","))
