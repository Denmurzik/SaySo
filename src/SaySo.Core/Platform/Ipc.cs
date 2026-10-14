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

    public static readonly string[] AllMessages = [ReloadSettings, SuspendHotkey, ResumeHotkey, OpenSettings, Quit];
}

public static class IpcClient
{
    public static bool IsDaemonRunning(string windowClass = IpcProtocol.WindowClass) => Find(windowClass) != 0;

    public static bool Send(string message, string windowClass = IpcProtocol.WindowClass)
    {
        var hwnd = Find(windowClass);
        return hwnd != 0 && Native.PostMessageW(hwnd, Native.RegisterWindowMessageW(message), 0, 0);
    }

    private static nint Find(string windowClass) => Native.FindWindowExW(0, 0, windowClass, null);
}
