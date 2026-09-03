param([string]$Password = "564D7EC4")

$ws = New-Object System.Net.WebSockets.ClientWebSocket
$ct = [System.Threading.CancellationToken]::None
$ws.ConnectAsync([Uri]"ws://localhost:28252/", $ct).Wait()

$buf = New-Object byte[] (8388608)
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

function Send-Text {
    param($ws, $ct, $text)
    $b = [System.Text.Encoding]::UTF8.GetBytes($text)
    $ws.SendAsync([ArraySegment[byte]]::new($b), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $ct).Wait()
}

# auth
$m = Receive-FullMessage $ws $ct $buf
Write-Output ("auth: " + $m.data)
Send-Text $ws $ct "auth:$Password"
$m = Receive-FullMessage $ws $ct $buf
Write-Output ("auth result: " + $m.data)

# request HEVC
Send-Text $ws $ct "codec:hevc"

$descCodec = ""
$descBytes = $null
$chunks = New-Object System.Collections.Generic.List[byte[]]
$keyframes = 0
$sw = [System.Diagnostics.Stopwatch]::StartNew()

while ($sw.ElapsedMilliseconds -lt 6000) {
    $left = 6000 - $sw.ElapsedMilliseconds
    $task = $ws.ReceiveAsync([ArraySegment[byte]]::new($buf), $ct)
    if (-not $task.Wait([Math]::Max($left, 1))) { break }
    $r = $task.Result
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
    $flags = $data[1]
    if ($codec -ne 2) { continue }

    $payload = $data[15..($data.Length - 1)]
    $chunks.Add($payload)
    if (($flags -band 0x01) -ne 0) { $keyframes++ }
}
$ws.Abort()

Write-Output ("chunks={0} keyframes={1}" -f $chunks.Count, $keyframes)
if ($chunks.Count -eq 0) { Write-Output "NO HEVC CHUNKS"; exit 1 }

# First chunk must start with a 4-byte NAL length, not a start code
$c0 = $chunks[0]
Write-Output ("first chunk bytes: {0:X2} {1:X2} {2:X2} {3:X2} {4:X2} {5:X2}" -f $c0[0], $c0[1], $c0[2], $c0[3], $c0[4], $c0[5])

# Reconstruct Annex-B elementary stream from length-prefixed NALs
$msOut = New-Object System.IO.MemoryStream
$startCode = [byte[]](0,0,0,1)
foreach ($payload in $chunks) {
    $pos = 0
    while ($pos + 4 -le $payload.Length) {
        $nalLen = ([int]$payload[$pos] -shl 24) -bor ([int]$payload[$pos+1] -shl 16) -bor ([int]$payload[$pos+2] -shl 8) -bor [int]$payload[$pos+3]
        if ($nalLen -le 0 -or ($pos + 4 + $nalLen) -gt $payload.Length) { break }
        $msOut.Write($startCode, 0, 4)
        $msOut.Write($payload, $pos + 4, $nalLen)
        $pos += 4 + $nalLen
    }
}
[System.IO.File]::WriteAllBytes("C:\Users\JulianB\AppData\Local\Temp\hevc_test.265", $msOut.ToArray())
Write-Output ("wrote hevc_test.265: {0} bytes" -f $msOut.Length)

if ($descCodec -ne "") { Write-Output ("desc codec: " + $descCodec) }
