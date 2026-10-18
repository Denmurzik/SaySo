namespace SaySo.Core.Hotkeys;

/// <summary>
/// macOS: коды клавиш Carbon (kVK_*, не зависят от раскладки) ↔ коды VK, которыми оперируют автомат горячей клавиши, запись
/// комбинации и settings.json. Так вся логика клавиш (<see cref="HotkeyStateMachine"/>, <see cref="HotkeyValidator"/>,
/// <see cref="HotkeyFormatter"/>) общая с Windows. Клавиша без пары получает код ≥ <see cref="UnmappedBase"/>: автомат
/// видит её как постороннюю (отмена удержания), а в настройки она не попадает (нормализатор пропускает только VK &lt; 255).
/// </summary>
public static class MacKeyCodes
{
    /// <summary>Код VK для клавиш без пары: <c>UnmappedBase + kVK</c>.</summary>
    public const int UnmappedBase = 0x100;

    // Модификаторы macOS приходят событием flagsChanged; нажата клавиша или отпущена — по битам NX_DEVICE*KEYMASK.
    public const int Command = 0x37;
    public const int RightCommand = 0x36;
    public const int Shift = 0x38;
    public const int RightShift = 0x3C;
    public const int Option = 0x3A;
    public const int RightOption = 0x3D;
    public const int Control = 0x3B;
    public const int RightControl = 0x3E;
    public const int Function = 0x3F;
    public const int CapsLock = 0x39;
    public const int Escape = 0x35;
    public const int V = 0x09;

    private static readonly Dictionary<int, int> ToVk = new()
    {
        // буквы (позиции ANSI)
        [0x00] = 'A', [0x0B] = 'B', [0x08] = 'C', [0x02] = 'D', [0x0E] = 'E', [0x03] = 'F', [0x05] = 'G', [0x04] = 'H',
        [0x22] = 'I', [0x26] = 'J', [0x28] = 'K', [0x25] = 'L', [0x2E] = 'M', [0x2D] = 'N', [0x1F] = 'O', [0x23] = 'P',
        [0x0C] = 'Q', [0x0F] = 'R', [0x01] = 'S', [0x11] = 'T', [0x20] = 'U', [0x09] = 'V', [0x0D] = 'W', [0x07] = 'X',
        [0x10] = 'Y', [0x06] = 'Z',

        // цифры верхнего ряда
        [0x1D] = '0', [0x12] = '1', [0x13] = '2', [0x14] = '3', [0x15] = '4', [0x17] = '5', [0x16] = '6', [0x1A] = '7',
        [0x1C] = '8', [0x19] = '9',

        // знаки (VK_OEM_*)
        [0x1B] = 0xBD, [0x18] = 0xBB, [0x21] = 0xDB, [0x1E] = 0xDD, [0x2A] = 0xDC, [0x29] = 0xBA, [0x27] = 0xDE,
        [0x2B] = 0xBC, [0x2F] = 0xBE, [0x2C] = 0xBF, [0x32] = 0xC0, [0x0A] = 0xE2,

        // служебные
        [0x24] = 0x0D, [0x30] = 0x09, [0x31] = 0x20, [0x33] = 0x08, [Escape] = VirtualKeys.Escape, [0x75] = 0x2E,
        [0x72] = 0x2D, [0x73] = 0x24, [0x77] = 0x23, [0x74] = 0x21, [0x79] = 0x22,
        [0x7B] = 0x25, [0x7E] = 0x26, [0x7C] = 0x27, [0x7D] = 0x28,

        // F1–F20
        [0x7A] = 0x70, [0x78] = 0x71, [0x63] = 0x72, [0x76] = 0x73, [0x60] = 0x74, [0x61] = 0x75, [0x62] = 0x76,
        [0x64] = 0x77, [0x65] = 0x78, [0x6D] = 0x79, [0x67] = 0x7A, [0x6F] = 0x7B, [0x69] = 0x7C, [0x6B] = 0x7D,
        [0x71] = 0x7E, [0x6A] = 0x7F, [0x40] = 0x80, [0x4F] = 0x81, [0x50] = 0x82, [0x5A] = 0x83,

        // цифровая клавиатура
        [0x52] = 0x60, [0x53] = 0x61, [0x54] = 0x62, [0x55] = 0x63, [0x56] = 0x64, [0x57] = 0x65, [0x58] = 0x66,
        [0x59] = 0x67, [0x5B] = 0x68, [0x5C] = 0x69, [0x43] = 0x6A, [0x45] = 0x6B, [0x4E] = 0x6D, [0x41] = 0x6E,
        [0x4B] = 0x6F,

        // модификаторы: Cmd — как Win, Option — как Alt
        [Command] = VirtualKeys.LWin, [RightCommand] = VirtualKeys.RWin,
        [Shift] = VirtualKeys.LShift, [RightShift] = VirtualKeys.RShift,
        [Option] = VirtualKeys.LMenu, [RightOption] = VirtualKeys.RMenu,
        [Control] = VirtualKeys.LControl, [RightControl] = VirtualKeys.RControl,
        [Function] = VirtualKeys.MacFunction,
    };

    private static readonly Dictionary<int, int> FromVk = ToVk.ToDictionary(p => p.Value, p => p.Key);

    /// <summary>
    /// Код VK для клавиши macOS. Caps Lock намеренно без пары: macOS сообщает только о переключении его состояния, а не о
    /// нажатии и отпускании, поэтому удерживать его как горячую клавишу нельзя.
    /// </summary>
    public static int ToVirtualKey(int keyCode) => ToVk.TryGetValue(keyCode, out var vk) ? vk : UnmappedBase + keyCode;

    /// <returns>Код клавиши macOS или -1, если у VK нет пары.</returns>
    public static int FromVirtualKey(int vk) =>
        FromVk.TryGetValue(vk, out var code) ? code : vk >= UnmappedBase && vk < UnmappedBase + 0x80 ? vk - UnmappedBase : -1;

    /// <summary>Клавиша-модификатор macOS (событие flagsChanged, а не keyDown/keyUp).</summary>
    public static bool IsModifierKey(int keyCode) =>
        keyCode is Command or RightCommand or Shift or RightShift or Option or RightOption or Control or RightControl or Function;

    /// <summary>
    /// Нажат ли модификатор по флагам события flagsChanged. Для левой и правой клавиши — свой бит NX_DEVICE*KEYMASK, поэтому
    /// отпускание одной при нажатой другой видно; у Fn такого бита нет, только общий NSEventModifierFlagFunction.
    /// </summary>
    public static bool IsModifierDown(int keyCode, ulong flags) => keyCode switch
    {
        Control => (flags & 0x0001) != 0,
        Shift => (flags & 0x0002) != 0,
        RightShift => (flags & 0x0004) != 0,
        Command => (flags & 0x0008) != 0,
        RightCommand => (flags & 0x0010) != 0,
        Option => (flags & 0x0020) != 0,
        RightOption => (flags & 0x0040) != 0,
        RightControl => (flags & 0x2000) != 0,
        Function => (flags & 0x0080_0000) != 0,
        _ => false,
    };
}
