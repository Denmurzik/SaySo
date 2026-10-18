using SaySo.Core.Input;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

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

    /// <summary>
    /// Демон запущен и его процесс не выше нашего по уровню целостности. Повышенному демону (режим «от администратора»)
    /// непривилегированное окно ничего не поручает: например, установку обновления он не делает, а только открывает страницу релиза.
    /// Уровень не удалось определить — считаем, что выше.
    /// </summary>
    public static bool IsDaemonNotMoreElevatedThanUs(string windowClass = IpcProtocol.WindowClass)
    {
        var hwnd = Find(windowClass);
        return hwnd != 0 && ProcessIntegrity.ForWindow(hwnd) is { } level && level <= ProcessIntegrity.Current;
    }

    private static nint Find(string windowClass) => Native.FindWindowExW(0, 0, windowClass, null);
}
