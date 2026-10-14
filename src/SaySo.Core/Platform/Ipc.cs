using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>
/// Протокол «окно настроек → демон»: зарегистрированные оконные сообщения скрытому окну демона.
/// Окно — верхнего уровня (не message-only), поэтому ищется через FindWindowEx(0, …).
/// </summary>
public static class IpcProtocol
{
    public const string WindowClass = "SaySo.Daemon.Ipc";
    public const string ReloadSettings = "SaySo.ReloadSettings";
    public const string SuspendHotkey = "SaySo.SuspendHotkey";
    public const string ResumeHotkey = "SaySo.ResumeHotkey";
    public const string OpenSettings = "SaySo.OpenSettings";
    public const string Quit = "SaySo.Quit";

    /// <summary>Окно настроек нашло новую версию: демон проверяет сам (без суточного ограничения, без уведомления).</summary>
    public const string CheckUpdates = "SaySo.CheckUpdates";

    /// <summary>Установить обновление: демон перепроверяет релиз, скачивает, проверяет и запускает Setup.</summary>
    public const string InstallUpdate = "SaySo.InstallUpdate";

    /// <summary>Сколько демон ждёт завершения диктовки при выходе (потом выходит принудительно).</summary>
    public static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(10);

    public static readonly string[] AllMessages = [ReloadSettings, SuspendHotkey, ResumeHotkey, OpenSettings, Quit, CheckUpdates, InstallUpdate];
}

public static class IpcClient
{
    public static bool IsDaemonRunning(string windowClass = IpcProtocol.WindowClass) => Find(windowClass) != 0;

    public static bool Send(string message, string windowClass = IpcProtocol.WindowClass)
    {
        var hwnd = Find(windowClass);
        if (hwnd == 0)
        {
            return false;
        }

        var id = Native.RegisterWindowMessageW(message);
        return id != 0 && Native.PostMessageW(hwnd, id, 0, 0);
    }

    private static nint Find(string windowClass) => Native.FindWindowExW(0, 0, windowClass, null);
}
