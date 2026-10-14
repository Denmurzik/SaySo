# Отправляет демону SaySo IPC-сообщение: ReloadSettings | SuspendHotkey | ResumeHotkey | OpenSettings | Quit
param([Parameter(Mandatory = $true)][string]$Message)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SaySoIpc {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, UIntPtr w, IntPtr l);
}
"@
# [NullString]::Value: обычный $null PowerShell превращает в пустую строку, и поиск шёл бы по пустому заголовку.
$hwnd = [SaySoIpc]::FindWindowExW([IntPtr]::Zero, [IntPtr]::Zero, 'SaySo.Daemon.Ipc', [NullString]::Value)
if ($hwnd -eq [IntPtr]::Zero) { Write-Host 'Демон SaySo не найден'; exit 1 }
[void][SaySoIpc]::PostMessageW($hwnd, [SaySoIpc]::RegisterWindowMessageW("SaySo.$Message"), [UIntPtr]::Zero, [IntPtr]::Zero)
Write-Host "Отправлено SaySo.$Message"
