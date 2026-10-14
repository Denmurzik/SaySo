# Снимок одного окна через PrintWindow — в кадр не попадает ничего, кроме этого окна.
# По умолчанию снимает окно настроек SaySo: заголовок «SaySo», класс окна WinUI 3 (WinUIDesktopWin32WindowClass).
# Снимок обрезается по видимой границе окна (без невидимой рамки Windows 11), пиксели за скруглёнными углами делаются прозрачными.
# Всплывающее меню (класс #32768, у него нет заголовка и скруглений): -Class '#32768' -Title '' -CornerRadius 0
param(
    [string]$Title = 'SaySo',
    [string]$Class = 'WinUIDesktopWin32WindowClass',
    [string]$Out = 'docs/assets/settings.png',
    [int]$CornerRadius = 8 # радиус скругления окна Windows 11 в DIP; 0 — не трогать углы
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SaySoCapture {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
"@
# Без DPI-осведомлённости GetWindowRect отдаёт «виртуальные» размеры, и снимок получится обрезанным при масштабе > 100 %.
# PerMonitorV2 (-4); на системах без этого вызова — системная осведомлённость.
try { $dpiOk = [SaySoCapture]::SetProcessDpiAwarenessContext([IntPtr]::new(-4)) } catch { $dpiOk = $false }
if (-not $dpiOk) { [void][SaySoCapture]::SetProcessDPIAware() }

$cls = if ($Class) { $Class } else { [NullString]::Value }
$hwnd = [SaySoCapture]::FindWindowW($cls, $Title)
if ($hwnd -eq [IntPtr]::Zero) { throw "Окно «$Title» (класс $Class) не найдено" }
if ([SaySoCapture]::IsIconic($hwnd)) { throw "Окно «$Title» свёрнуто — разверните его и повторите" }
$r = New-Object SaySoCapture+RECT
if (-not [SaySoCapture]::GetWindowRect($hwnd, [ref]$r)) { throw 'GetWindowRect не удался' }

$full = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T), ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
try {
    $g = [System.Drawing.Graphics]::FromImage($full)
    try {
        $hdc = $g.GetHdc()
        try {
            $printed = [SaySoCapture]::PrintWindow($hwnd, $hdc, 2) # PW_RENDERFULLCONTENT — нужен для WinUI/DirectComposition
        }
        finally {
            $g.ReleaseHdc($hdc)
        }
    }
    finally {
        $g.Dispose()
    }
    if (-not $printed) { throw "PrintWindow не удался для окна «$Title»" }

    # В Windows 11 у окна есть невидимая рамка изменения размера: обрезаем снимок по видимой границе (DWMWA_EXTENDED_FRAME_BOUNDS).
    $crop = New-Object System.Drawing.Rectangle 0, 0, $full.Width, $full.Height
    $v = New-Object SaySoCapture+RECT
    $cropped = $false
    if ([SaySoCapture]::DwmGetWindowAttribute($hwnd, 9, [ref]$v, [Runtime.InteropServices.Marshal]::SizeOf($v)) -eq 0) {
        $c = New-Object System.Drawing.Rectangle ($v.L - $r.L), ($v.T - $r.T), ($v.R - $v.L), ($v.B - $v.T)
        if ($c.X -ge 0 -and $c.Y -ge 0 -and $c.Width -gt 0 -and $c.Height -gt 0 -and $c.Right -le $full.Width -and $c.Bottom -le $full.Height) {
            $crop = $c
            $cropped = $true
        }
    }
    if (-not $cropped) { Write-Warning 'Видимую границу окна получить не удалось — снимок не обрезан, по краям может быть невидимая рамка.' }
    $bmp = $full.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
}
finally {
    $full.Dispose()
}

try {
    # Пиксели за скруглёнными углами окна (PrintWindow закрашивает их чёрным) — прозрачные, край сглаживается.
    if ($CornerRadius -gt 0 -and $cropped -and -not [SaySoCapture]::IsZoomed($hwnd)) {
        try { $dpi = [SaySoCapture]::GetDpiForWindow($hwnd) } catch { $dpi = 96 }
        if ($dpi -le 0) { $dpi = 96 }
        $rad = [int][math]::Round($CornerRadius * $dpi / 96)
        $w = $bmp.Width
        $h = $bmp.Height
        for ($dy = 0; $dy -lt $rad; $dy++) {
            for ($dx = 0; $dx -lt $rad; $dx++) {
                $d = [math]::Sqrt([math]::Pow($rad - $dx - 0.5, 2) + [math]::Pow($rad - $dy - 0.5, 2)) # расстояние от центра угла до центра пикселя
                if ($d -le $rad - 0.5) { continue }
                $k = [math]::Max(0.0, [math]::Min(1.0, $rad + 0.5 - $d))
                foreach ($p in @(@($dx, $dy), @(($w - 1 - $dx), $dy), @($dx, ($h - 1 - $dy)), @(($w - 1 - $dx), ($h - 1 - $dy)))) {
                    $px = $bmp.GetPixel($p[0], $p[1])
                    $bmp.SetPixel($p[0], $p[1], [System.Drawing.Color]::FromArgb([int][math]::Round($px.A * $k), $px.R, $px.G, $px.B))
                }
            }
        }
    }

    $dir = Split-Path $Out
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bmp.Dispose()
}
Write-Host "OK $Out"
