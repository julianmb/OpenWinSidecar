param()
function L ([double]$c) { if ($c -le 0.04045) { return $c / 12.92 } return [Math]::Pow((($c + 0.055) / 1.055), 2.4) }
function Lum2 ([int]$r, [int]$g, [int]$b) { return (0.2126 * (L ($r/255.0))) + (0.7152 * (L ($g/255.0))) + (0.0722 * (L ($b/255.0))) }
function Ratio ([string]$fg, [string]$bg) {
    $f = @(); $b = @()
    for ($i = 0; $i -lt 3; $i++) { $f += [Convert]::ToInt32($fg.Substring($i*2, 2), 16); $b += [Convert]::ToInt32($bg.Substring($i*2, 2), 16) }
    $l1 = Lum2 $f[0] $f[1] $f[2]; $l2 = Lum2 $b[0] $b[1] $b[2]
    $hi = [Math]::Max($l1, $l2); $lo = [Math]::Min($l1, $l2)
    return [Math]::Round(($hi + 0.05) / ($lo + 0.05), 2)
}

Write-Output "ACCENT BUTTON candidates (text on accent, need >= 4.5):"
foreach ($bg in @("3B82F6", "2E6BE0", "2563EB", "1E5FD6", "1A55C4")) {
    $r = Ratio "FFFFFF" $bg
    Write-Output ("  white on #{0}: {1}" -f $bg, $r)
}

Write-Output "SECONDARY TEXT candidates (on card 1F1F25 / window 16161A):"
foreach ($fg in @("9A9AA3", "ADADB8", "B8B8C2", "C2C2CC", "CFCFD8")) {
    Write-Output ("  #{0}: card {1}  window {2}" -f $fg, (Ratio $fg "1F1F25"), (Ratio $fg "16161A"))
}

Write-Output "BORDER candidates (vs window 16161A, non-text needs >= 3.0):"
foreach ($fg in @("2C2C34", "34343E", "3A3A46", "42424E", "4A4A58")) {
    Write-Output ("  #{0}: {1}" -f $fg, (Ratio $fg "16161A"))
}

Write-Output "FIELD BORDER candidates (vs field 141418):"
foreach ($fg in @("34343E", "3E3E4A", "4A4A58")) {
    Write-Output ("  #{0}: {1}" -f $fg, (Ratio $fg "141418"))
}
