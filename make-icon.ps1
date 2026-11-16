# Draws BlackScreen.ico: a black monitor shape with a thin grey bezel, PNG-in-ICO at 256/48/32/16.
Add-Type -AssemblyName System.Drawing
$sizes = 256, 48, 32, 16
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $m = [Math]::Max(1, [int]($s * 0.06))
    $bez = [Math]::Max(1, [int]($s * 0.05))
    $h = [int]($s * 0.68)
    $grey = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 140, 140, 150))
    $g.FillRectangle($grey, $m, $m, $s - 2 * $m, $h)                      # bezel
    $g.FillRectangle([System.Drawing.Brushes]::Black, $m + $bez, $m + $bez, $s - 2 * ($m + $bez), $h - 2 * $bez)  # screen
    $sw = [Math]::Max(1, [int]($s * 0.12))
    $g.FillRectangle($grey, [int](($s - $sw) / 2), $m + $h, $sw, [int]($s * 0.12))          # stand neck
    $g.FillRectangle($grey, [int]($s * 0.28), $m + $h + [int]($s * 0.12), [int]($s * 0.44), [Math]::Max(1, [int]($s * 0.06)))  # base
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $PSScriptRoot 'BlackScreen.ico'), $out.ToArray())
