# Рисует иконку SaySo (градиентный скруглённый квадрат + глиф микрофона Segoe Fluent Icons)
# и сохраняет src/SaySo/Assets/SaySo.ico (PNG-кадры 16–256) и docs/assets/icon.png (256×256).
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $g.Clear([System.Drawing.Color]::Transparent)
    $r = [Math]::Max(2.0, $s * 0.22)
    $d = 2 * $r
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
    $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90)
    $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90)
    $path.CloseFigure()
    $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 91, 91, 247)), ([System.Drawing.Color]::FromArgb(255, 168, 85, 247)), 45.0
    $g.FillPath($brush, $path)
    $font = New-Object System.Drawing.Font 'Segoe Fluent Icons', ([float]($s * 0.56)), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = [System.Drawing.StringAlignment]::Center
    $sf.LineAlignment = [System.Drawing.StringAlignment]::Center
    $g.DrawString([string][char]0xE720, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, ($s * 0.04), $s, $s), $sf)
    $g.Dispose()
    return $bmp
}

$root = Join-Path $PSScriptRoot '..'
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$frames = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) {
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'docs/assets') | Out-Null
        $bmp.Save((Join-Path $root 'docs/assets/icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
    ,@($s, $ms.ToArray())
}

$icoPath = Join-Path $root 'src/SaySo/Assets/SaySo.ico'
New-Item -ItemType Directory -Force -Path (Split-Path $icoPath) | Out-Null
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
$w.Close()
Write-Host "OK $icoPath"
