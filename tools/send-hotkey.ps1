# Имитирует удержание клавиши (по умолчанию правый Ctrl) для проверки демона. Нажатия не помечены маркером SaySo.
param([int]$HoldMs = 600, [int]$Vk = 0xA3)
$ErrorActionPreference = 'Stop'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class SaySoKeys {
    [StructLayout(LayoutKind.Sequential)] public struct KI { public ushort vk; public ushort scan; public uint flags; public uint time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] public struct MI { public int dx; public int dy; public uint data; public uint flags; public uint time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public KI ki; [FieldOffset(0)] public MI mi; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public U u; }
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    public static void Key(ushort vk, bool up) {
        var i = new INPUT { type = 1 };
        i.u.ki.vk = vk;
        i.u.ki.flags = (uint)((up ? 2 : 0) | ((vk == 0xA3 || vk == 0xA5) ? 1 : 0));
        SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }
}
"@
[SaySoKeys]::Key([uint16]$Vk, $false)
Start-Sleep -Milliseconds $HoldMs
[SaySoKeys]::Key([uint16]$Vk, $true)
Write-Host "Клавиша 0x$('{0:X2}' -f $Vk) удерживалась $HoldMs мс"
