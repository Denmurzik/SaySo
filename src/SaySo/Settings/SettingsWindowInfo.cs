namespace SaySo.Settings;

/// <summary>Общие константы окна настроек (заголовок и класс окна нужны и окну, и поиску существующего окна в Program).</summary>
internal static class SettingsWindowInfo
{
    public const string Title = "SaySo — настройки";

    /// <summary>Класс окна верхнего уровня WinUI 3 (Win32-обёртка Window); сужает поиск до окон WinUI с этим заголовком.</summary>
    public const string WindowClass = "WinUIDesktopWin32WindowClass";
}
