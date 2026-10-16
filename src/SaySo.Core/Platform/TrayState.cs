namespace SaySo.Core.Platform;

/// <summary>Состояние значка SaySo (трей Windows, строка меню macOS).</summary>
public enum TrayState
{
    Idle,
    Recording,
    Paused,
    Error,
}
