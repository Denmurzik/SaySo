namespace SaySo.Core.Hotkeys;

/// <summary>Событие клавиатуры из LL-хука. TimestampMs — тики хука (uint, переполняются).</summary>
public readonly record struct KeyEvent(int VirtualKey, bool IsDown, bool IsInjected, long TimestampMs);

public enum HotkeyAction
{
    None,
    StartRecording,
    StopRecording,
    Cancel,
}

/// <summary>Решение автомата: действие и нужно ли поглотить клавишу (не пускать дальше).</summary>
public readonly record struct HotkeyDecision(HotkeyAction Action, bool Swallow)
{
    public static readonly HotkeyDecision Pass = new(HotkeyAction.None, false);
}
