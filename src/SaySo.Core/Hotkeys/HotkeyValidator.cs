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

        var hasNonShiftModifier = keys.Any(vk => VirtualKeys.IsModifier(vk) && !VirtualKeys.IsShift(vk));
        if (hasNonShiftModifier)
        {
            return null;
        }

        if (keys.Count == 1 && IsStandaloneKey(keys.First()))
        {
            return null;
        }

        return "Такая комбинация будет срабатывать при обычном наборе текста. " +
               "Используйте Ctrl, Alt или Win, либо отдельную клавишу вроде правого Ctrl, Caps Lock или клавиш от F13 до F24.";
    }

    // Отдельные клавиши, которые не участвуют в наборе текста.
    private static bool IsStandaloneKey(int vk) =>
        vk is 0x14 or 0x91 or 0x13 or 0x2D or 0x5D or (>= 0x70 and <= 0x87);
}
