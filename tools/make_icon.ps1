param([string]$OutPath = "$PSScriptRoot\..\src\OpenWinSidecar\app.ico")

Add-Type -AssemblyName System.Drawing

# Design: rounded dark plate, accent-blue monitor with white signal dot + wave arcs,
# stand below — a remote display being watched from elsewhere. Solid fills only
# (robust at 16px, no gradient-brush pitfalls in PowerShell).
function New-IconBitmap {
    param([int]$Size)

    $bmp = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $dark  = [System.Drawing.Color]::FromArgb(255, 24, 24, 30)    # #18181E
    $blue  = [System.Drawing.Color]::FromArgb(255, 59, 130, 246)   # #3B82F6
    $white = [System.Drawing.Color]::White
    $stand = [System.Drawing.Color]::FromArgb(255, 148, 163, 184)  # slate

    $u = $Size / 256.0   # design unit

    function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
        $p = New-Object System.Drawing.Drawing2D.GraphicsPath
        $p.AddArc($x, $y, $r, $r, 180, 90)
        $p.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
        $p.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90)
        $p.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
        $p.CloseFigure()
        return $p
    }

    # Rounded-square plate
    $plate = RoundRect 0 0 $Size $Size (44*$u)
    $plateBrush = New-Object System.Drawing.SolidBrush($dark)
    $g.FillPath($plateBrush, $plate)

    # Monitor (48..208 x 56..172)
    $monX = 48*$u; $monY = 56*$u; $monW = 160*$u; $monH = 116*$u
    $mon = RoundRect $monX $monY $monW $monH (14*$u)
    $monBrush = New-Object System.Drawing.SolidBrush($blue)
    $g.FillPath($monBrush, $mon)

    # Signal dot + wave arcs centered on the screen
    $cx = $monX + $monW/2
    $cy = $monY + $monH/2

    $penW = [Math]::Max(1.5, 10*$u)
    $wavePen = New-Object System.Drawing.Pen($white, $penW)
    $wavePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $wavePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $g.DrawArc($wavePen, ($cx - 54*$u), ($cy - 36*$u), (46*$u), (72*$u), 300, 120)
    $g.DrawArc($wavePen, ($cx + 8*$u),  ($cy - 36*$u), (46*$u), (72*$u), 120, 120)

    $dotR = 12 * $u
    $dotBrush = New-Object System.Drawing.SolidBrush($white)
    $g.FillEllipse($dotBrush, ($cx - $dotR), ($cy - $dotR), ($dotR*2), ($dotR*2))

    # Stand
    $standBrush = New-Object System.Drawing.SolidBrush($stand)
    $g.FillRectangle($standBrush, ($cx - 26*$u), ($monY + $monH), (52*$u), (16*$u))
    $g.FillRectangle($standBrush, ($cx - 50*$u), ($monY + $monH + 14*$u), (100*$u), (16*$u))

    $g.Dispose()
    return $bmp
}

$sizes = @(16, 20, 24, 32, 48, 64, 128, 256)

$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += ,([byte[]]$ms.ToArray())
    $ms.Dispose()
}

$fs = [System.IO.File]::Create($OutPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$w)
    $bw.Write([byte]$w)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$pngs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}

foreach ($png in $pngs) { $bw.Write($png) }

$bw.Flush()
$bw.Close()

$fi = Get-Item $OutPath
Write-Output ("ICO written: {0} ({1} bytes, sizes: {2})" -f $OutPath, $fi.Length, ($sizes -join ","))

# Also export a large PNG preview for visual verification
$prev = New-IconBitmap 256
$prev.Save("C:\Users\JulianB\AppData\Local\Temp\icon_preview.png", [System.Drawing.Imaging.ImageFormat]::Png)
$prev.Dispose()
Write-Output "Preview: C:\Users\JulianB\AppData\Local\Temp\icon_preview.png"
