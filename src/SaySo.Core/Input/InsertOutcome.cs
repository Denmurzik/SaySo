namespace SaySo.Core.Input;

/// <summary>Чем закончилась вставка текста в активное окно (Windows: TextInserter, macOS: вставка через NSPasteboard).</summary>
public enum InsertOutcome
{
    Inserted,
    BlockedByElevation,
    ClipboardUnavailable,
    InputBlocked,
    NoTarget,

    /// <summary>macOS: SaySo не разрешён «Универсальный доступ», нажатие Cmd+V не дойдёт до окна; текст оставлен в буфере.</summary>
    AccessibilityDenied,
}
