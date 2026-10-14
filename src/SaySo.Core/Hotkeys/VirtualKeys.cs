namespace SaySo.Core.Hotkeys;

/// <summary>Коды виртуальных клавиш, которые нужны SaySo.</summary>
public static class VirtualKeys
{
    public const int Escape = 0x1B;
    public const int LShift = 0xA0;
    public const int RShift = 0xA1;
    public const int LControl = 0xA2;
    public const int RControl = 0xA3;
    public const int LMenu = 0xA4;
    public const int RMenu = 0xA5;
    public const int LWin = 0x5B;
    public const int RWin = 0x5C;

    /// <summary>Неназначенная клавиша: её нажатие между Win/Alt down и up не даёт открыться «Пуску»/меню.</summary>
    public const int MenuMaskKey = 0xE8;

    public static bool IsShift(int vk) => vk is LShift or RShift or 0x10;

    public static bool IsModifier(int vk) =>
        vk is LShift or RShift or LControl or RControl or LMenu or RMenu or LWin or RWin or 0x10 or 0x11 or 0x12;
}
