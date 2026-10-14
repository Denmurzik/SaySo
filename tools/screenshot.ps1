# Снимок нижней трети основного экрана (для проверки оверлея).
param([string]$Out = 'artifacts/overlay.png')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$h = [int]($b.Height / 3)
$bmp = New-Object System.Drawing.Bitmap $b.Width, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.Left, $b.Bottom - $h, 0, 0, $bmp.Size)
New-Item -ItemType Directory -Force -Path (Split-Path $Out) | Out-Null
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "OK $Out"
