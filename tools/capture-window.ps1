# Снимок одного окна через PrintWindow — в кадр не попадает ничего, кроме этого окна.
# По умолчанию снимает окно настроек SaySo: заголовок «SaySo», класс окна WinUI 3 (WinUIDesktopWin32WindowClass).
param(
    [string]$Title = 'SaySo',
    [string]$Class = 'WinUIDesktopWin32WindowClass',
    [string]$Out = 'docs/assets/settings.png'
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
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
}
"@
# Без DPI-осведомлённости GetWindowRect отдаёт «виртуальные» размеры, и снимок получится обрезанным при масштабе > 100 %.
[void][SaySoCapture]::SetProcessDPIAware()
$cls = if ($Class) { $Class } else { [NullString]::Value }
$hwnd = [SaySoCapture]::FindWindowW($cls, $Title)
if ($hwnd -eq [IntPtr]::Zero) { throw "Окно «$Title» (класс $Class) не найдено" }
if ([SaySoCapture]::IsIconic($hwnd)) { throw "Окно «$Title» свёрнуто — разверните его и повторите" }
$r = New-Object SaySoCapture+RECT
[void][SaySoCapture]::GetWindowRect($hwnd, [ref]$r)
$full = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($full)
$hdc = $g.GetHdc()
[void][SaySoCapture]::PrintWindow($hwnd, $hdc, 2) # PW_RENDERFULLCONTENT — нужен для WinUI/DirectComposition
$g.ReleaseHdc($hdc)
$g.Dispose()

# В Windows 11 у окна есть невидимая рамка изменения размера: обрезаем снимок по видимой границе (DWMWA_EXTENDED_FRAME_BOUNDS).
$v = New-Object SaySoCapture+RECT
$crop = New-Object System.Drawing.Rectangle 0, 0, $full.Width, $full.Height
if ([SaySoCapture]::DwmGetWindowAttribute($hwnd, 9, [ref]$v, [Runtime.InteropServices.Marshal]::SizeOf($v)) -eq 0) {
    $c = New-Object System.Drawing.Rectangle ($v.L - $r.L), ($v.T - $r.T), ($v.R - $v.L), ($v.B - $v.T)
    if ($c.X -ge 0 -and $c.Y -ge 0 -and $c.Width -gt 0 -and $c.Height -gt 0 -and $c.Right -le $full.Width -and $c.Bottom -le $full.Height) { $crop = $c }
}
$bmp = $full.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$full.Dispose()
$dir = Split-Path $Out
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Host "OK $Out"
