param([int]$DepthTest = 0) # 0 = with -async_depth 1, 1 = without

$ff = "C:\Users\JulianB\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0.1-full_build\bin\ffmpeg.exe"

$argsStr = "-hide_banner -loglevel error -init_hw_device qsv=hw -filter_hw_device hw " +
           "-f rawvideo -pix_fmt bgra -s 1180x664 -r 60 -i pipe:0 " +
           "-c:v hevc_qsv -preset veryfast -b:v 8000k -maxrate 12000k -bufsize 2000k " +
           "-g 240 -bf 0 "
if ($DepthTest -eq 0) { $argsStr += "-async_depth 1 " }
$argsStr += "-an -f hevc pipe:1"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $ff
$psi.Arguments = $argsStr
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$p = [System.Diagnostics.Process]::Start($psi)

$outBytes = 0
$readTask = [System.Threading.Tasks.Task]::Run([Func[int]] {
    param() 0
})

# background stdout reader
$readerJob = {
    param($std, $state)
    $buf = New-Object byte[] (65536)
    $total = 0
    try {
        while ($true) {
            $n = $std.Read($buf, 0, $buf.Length)
            if ($n -le 0) { break }
            $total += $n
            $state.Total = $total
            $state.Updates++
        }
    } catch { }
}
$state = New-Object PSObject -Property @{ Total = 0; Updates = 0 }
$ps = [powershell]::Create()
$ps.AddScript($readerJob.ToString()).AddArgument($p.StandardOutput.BaseStream).AddArgument($state) | Out-Null
$handle = $ps.BeginInvoke()

$stdin = $p.StandardInput.BaseStream
$frame = New-Object byte[] (1180 * 664 * 4)
# deterministic-ish content
for ($i = 0; $i -lt $frame.Length; $i += 4096) { $frame[$i] = [byte](($i / 4096) % 255) }

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$frameCount = 0
$lastReport = 0

# Feed 8 seconds at 60fps realtime
while ($sw.ElapsedMilliseconds -lt 8000) {
    $stdin.Write($frame, 0, $frame.Length)
    $stdin.Flush()
    $frameCount++

    if (($sw.ElapsedMilliseconds - $lastReport) -ge 1000) {
        Write-Output ("t={0:F1}s framesIn={1} bytesOut={2}" -f ($sw.ElapsedMilliseconds / 1000.0), $frameCount, $state.Total)
        $lastReport = $sw.ElapsedMilliseconds
    }
    Start-Sleep -Milliseconds 16
}

Write-Output ("FINAL: framesIn={0} bytesOut={1} exited={2}" -f $frameCount, $state.Total, $p.HasExited)
$stdin.Close()
Start-Sleep -Milliseconds 800
Write-Output ("AFTER EOF: bytesOut={0}" -f $state.Total)
$p.Kill()
