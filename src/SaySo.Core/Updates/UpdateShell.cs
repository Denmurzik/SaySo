using System.ComponentModel;
using System.Diagnostics;
using SaySo.Core.Diagnostics;

namespace SaySo.Core.Updates;

public enum InstallerLaunchResult
{
    Started,

    /// <summary>Пользователь отказал в запросе UAC.</summary>
    Cancelled,
    Failed,
}

/// <summary>Запуск Setup и открытие страницы релиза через оболочку.</summary>
public static class UpdateShell
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: отказ в окне UAC

    /// <param name="elevate">true — ShellExecute <c>runas</c> (установка «для всех»), иначе без повышения.</param>
    public static InstallerLaunchResult LaunchInstaller(string path, string arguments, bool elevate)
    {
        try
        {
            var info = new ProcessStartInfo(path, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            };
            if (elevate)
            {
                info.Verb = "runas";
            }

            Process.Start(info)?.Dispose();
            return InstallerLaunchResult.Started;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return InstallerLaunchResult.Cancelled;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Error("Запуск установщика обновления", e);
            return InstallerLaunchResult.Failed;
        }
    }

    /// <summary>Открывает страницу релиза в браузере; только https.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Error("Открытие страницы релиза", e);
        }
    }
}
