namespace SaySo.Core.Hotkeys;

/// <summary>Проверка комбинации перед сохранением: она не должна мешать обычному набору текста.</summary>
public static class HotkeyValidator
{
    public const int MaxKeys = 4;

    public static string? Validate(IReadOnlyCollection<int> keys)
    {
        if (keys.Count == 0)
        {
            return "Комбинация пуста. Нажмите клавишу или сочетание.";
        }

        if (keys.Contains(VirtualKeys.Escape))
        {
            return "Esc зарезервирован для отмены диктовки.";
        }

        if (keys.Count > MaxKeys)
        {
            return $"Слишком много клавиш (не больше {MaxKeys}).";
        }

        var mac = OperatingSystem.IsMacOS();
        var hasNonShiftModifier = keys.Any(vk => (VirtualKeys.IsModifier(vk) && !VirtualKeys.IsShift(vk))
                                                 || (mac && vk == VirtualKeys.MacFunction));
        if (hasNonShiftModifier)
        {
            return null;
        }

        if (keys.Count == 1 && IsStandaloneKey(keys.First(), mac))
        {
            return null;
        }

        return mac
            ? "Такая комбинация будет срабатывать при обычном наборе текста. " +
              "Используйте Control, Option или Cmd либо отдельную клавишу, например правый Cmd, Fn или F13."
            : "Такая комбинация будет срабатывать при обычном наборе текста. " +
              "Используйте Ctrl, Alt или Win либо отдельную клавишу, например правый Ctrl, Caps Lock или F13.";
    }

    // Отдельные клавиши, которые не участвуют в наборе текста. На macOS нет Caps Lock (о нём приходит только переключение),
    // Scroll Lock, Pause и Меню, зато есть Fn.
    private static bool IsStandaloneKey(int vk, bool mac) => mac
        ? vk is VirtualKeys.MacFunction or 0x2D or (>= 0x70 and <= 0x87)
        : vk is 0x14 or 0x91 or 0x13 or 0x2D or 0x5D or (>= 0x70 and <= 0x87);
}
