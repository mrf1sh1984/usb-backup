# Draws the app icon and writes src\UsbBackup.App\app.ico (16 to 256 px) plus a preview PNG.
# Run:  powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$icoPath = Join-Path $root 'src\UsbBackup.App\app.ico'

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 64.0   # everything below is designed on a 64 x 64 grid

    # Rounded blue tile
    $r = 14 * $s; $d = 2 * $r
    $x = 2 * $s; $y = 2 * $s; $w = 60 * $s
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tile.AddArc($x, $y, $d, $d, 180, 90)
    $tile.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $tile.AddArc($x + $w - $d, $y + $w - $d, $d, $d, 0, 90)
    $tile.AddArc($x, $y + $w - $d, $d, $d, 90, 90)
    $tile.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF $x, $y, $w, $w
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, `
        ([System.Drawing.Color]::FromArgb(0x3B, 0x82, 0xE6)), ([System.Drawing.Color]::FromArgb(0x17, 0x4A, 0x96)), 90.0
    $g.FillPath($fill, $tile)

    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)

    # Arrow pointing down into the drive: shaft + head
    $g.FillRectangle($white, [single](27 * $s), [single](11 * $s), [single](10 * $s), [single](15 * $s))
    $head = @(
        (New-Object System.Drawing.PointF ([single](17 * $s)), ([single](25 * $s))),
        (New-Object System.Drawing.PointF ([single](47 * $s)), ([single](25 * $s))),
        (New-Object System.Drawing.PointF ([single](32 * $s)), ([single](39 * $s)))
    )
    $g.FillPolygon($white, $head)

    # The drive: a rounded bar with an activity light
    $br = 4 * $s; $bd = 2 * $br
    $bx = 12 * $s; $by = 43 * $s; $bw = 40 * $s; $bh = 11 * $s
    $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bar.AddArc($bx, $by, $bd, $bd, 180, 90)
    $bar.AddArc($bx + $bw - $bd, $by, $bd, $bd, 270, 90)
    $bar.AddArc($bx + $bw - $bd, $by + $bh - $bd, $bd, $bd, 0, 90)
    $bar.AddArc($bx, $by + $bh - $bd, $bd, $bd, 90, 90)
    $bar.CloseFigure()
    $g.FillPath($white, $bar)
    if ($size -ge 24) {
        $light = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x22, 0xC5, 0x5E))
        $g.FillEllipse($light, [single](42 * $s), [single](46 * $s), [single](5 * $s), [single](5 * $s))
    }
    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $root 'tools\icon-preview.png')) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: header, one 16-byte directory entry per image, then the PNG data.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $w.Write($image) }
$w.Flush()
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
"Wrote $icoPath ($($out.Length) bytes, sizes: $($sizes -join ', '))"
