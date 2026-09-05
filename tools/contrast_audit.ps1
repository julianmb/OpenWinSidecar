param()

function L([double]$c) {
    if ($c -le 0.04045) { return $c / 12.92 }
    return [Math]::Pow((($c + 0.055) / 1.055), 2.4)
}
function Lum([int]$r, [int]$g, [int]$b) {
    return (0.2126 * ($r/255.0 | ForEach-Object { L $_ })) + 0 * 0
}
function Lum2([int]$r, [int]$g, [int]$b) {
    $lr = 0.2126 * (L ($r/255.0))
    $lg = 0.7152 * (L ($g/255.0))
    $lb = 0.0722 * (L ($b/255.0))
    return $lr + $lg + $lb
}
function Ratio([string]$fg, [string]$bg) {
    $f = @()
    $b = @()
    for ($i = 0; $i -lt 3; $i++) {
        $f += [Convert]::ToInt32($fg.Substring($i*2, 2), 16)
        $b += [Convert]::ToInt32($bg.Substring($i*2, 2), 16)
    }
    $l1 = Lum2 $f[0] $f[1] $f[2]
    $l2 = Lum2 $b[0] $b[1] $b[2]
    $hi = [Math]::Max($l1, $l2); $lo = [Math]::Min($l1, $l2)
    return [Math]::Round(($hi + 0.05) / ($lo + 0.05), 2)
}

$pairs = @(
    @{ name = "secondary text on card";      fg = "9A9AA3"; bg = "1F1F25" },
    @{ name = "secondary text on window";    fg = "9A9AA3"; bg = "16161A" },
    @{ name = "secondary text on field";     fg = "9A9AA3"; bg = "141418" },
    @{ name = "primary text on card";        fg = "E8E8EA"; bg = "1F1F25" },
    @{ name = "primary text on button";      fg = "E8E8EA"; bg = "26262E" },
    @{ name = "primary text on accent btn"; fg = "FFFFFF"; bg = "3B82F6" },
    @{ name = "GOOD blue on card";           fg = "7CB7FF"; bg = "1F1F25" },
    @{ name = "GOOD blue on GoodBg navy";    fg = "7CB7FF"; bg = "1B3A5C" },
    @{ name = "BAD yellow on card";          fg = "F2C94C"; bg = "1F1F25" },
    @{ name = "BAD yellow on BadBg olive";   fg = "F2C94C"; bg = "4A3F14" },
    @{ name = "card border on window";       fg = "2C2C34"; bg = "16161A" },
    @{ name = "button text on hover";        fg = "E8E8EA"; bg = "30303A" }
)

Write-Output ("{0,-34} {1,6}   {2}" -f "pair", "ratio", "AA (>=4.5 text)")
Write-Output ("-" * 60)
foreach ($p in $pairs) {
    $r = Ratio $p.fg $p.bg
    $ok = if ($r -ge 4.5) { "PASS" } else { "FAIL" }
    Write-Output ("{0,-34} {1,6}   {2}" -f $p.name, $r, $ok)
}
