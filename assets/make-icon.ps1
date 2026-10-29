# Draws the folder-with-arrow icon (same shape as MainForm.MakeIcon) at several sizes into FileFollow.ico
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot 'FileFollow.ico'
$sizes = 16, 24, 32, 48, 64, 256
$pngs = @()
foreach ($n in $sizes) {
    $k = $n / 32.0
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(43, 108, 176))
    $g.FillRectangle($blue, 2 * $k, 6 * $k, 13 * $k, 6 * $k)
    $g.FillRectangle($blue, 2 * $k, 9 * $k, 28 * $k, 19 * $k)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.5, 3 * $k))
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawLine($pen, 11 * $k, 19 * $k, 21 * $k, 19 * $k)
    $g.DrawLine($pen, 17 * $k, 14 * $k, 22 * $k, 19 * $k)
    $g.DrawLine($pen, 17 * $k, 24 * $k, 22 * $k, 19 * $k)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngs += , $ms.ToArray()
}
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $d = $pngs[$i]
    $w.Write([byte]($n % 256)); $w.Write([byte]($n % 256))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$d.Length); $w.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Close()
Write-Output "OK: $out"
