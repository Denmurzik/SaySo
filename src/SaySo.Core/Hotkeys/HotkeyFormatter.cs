namespace SaySo.Core.Hotkeys;

/// <summary>Человекочитаемое имя комбинации («Правый Ctrl», «Левый Ctrl + Левый Win + Пробел»).</summary>
public static class HotkeyFormatter
{
    public static string Format(IEnumerable<int> keys) =>
        string.Join(" + ", keys.OrderBy(Order).ThenBy(vk => vk).Select(Name));

    public static string Name(int vk) => OperatingSystem.IsMacOS() ? MacName(vk) : WindowsName(vk);

    /// <summary>Имена клавиш macOS: Cmd вместо Win, Option вместо Alt, Control вместо Ctrl.</summary>
    public static string MacName(int vk) => vk switch
    {
        0xA2 => "Левый Control",
        0xA3 => "Правый Control",
        0x11 => "Control",
        0xA4 => "Левый Option",
        0xA5 => "Правый Option",
        0x12 => "Option",
        0x5B => "Левый Cmd",
        0x5C => "Правый Cmd",
        VirtualKeys.MacFunction => "Fn",
        0x08 => "Delete",
        0x2D => "Help",
        _ => WindowsName(vk),
    };

    private static string WindowsName(int vk) => vk switch
    {
        0xA2 => "Левый Ctrl",
        0xA3 => "Правый Ctrl",
        0x11 => "Ctrl",
        0xA4 => "Левый Alt",
        0xA5 => "Правый Alt",
        0x12 => "Alt",
        0xA0 => "Левый Shift",
        0xA1 => "Правый Shift",
        0x10 => "Shift",
        0x5B => "Левый Win",
        0x5C => "Правый Win",
        0x14 => "Caps Lock",
        0x91 => "Scroll Lock",
        0x13 => "Pause",
        0x2D => "Insert",
        0x5D => "Меню",
        0x20 => "Пробел",
        0x1B => "Esc",
        0x09 => "Tab",
        0x0D => "Enter",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        (>= 0x30 and <= 0x39) or (>= 0x41 and <= 0x5A) => ((char)vk).ToString(),
        _ => $"0x{vk:X2}",
    };

    private static int Order(int vk) => vk switch
    {
        0xA2 or 0xA3 or 0x11 => 0,
        0xA4 or 0xA5 or 0x12 => 1,
        0xA0 or 0xA1 or 0x10 => 2,
        0x5B or 0x5C => 3,
        VirtualKeys.MacFunction => 4,
        _ => 5,
    };
}
