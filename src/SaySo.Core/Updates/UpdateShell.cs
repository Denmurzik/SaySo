using System.ComponentModel;
using System.Diagnostics;
using SaySo.Core.Diagnostics;
using SaySo.Core.Input;
using SaySo.Core.Platform;

namespace SaySo.Core.Updates;

public enum InstallerLaunchResult
{
    /// <summary>Setup запустился и завершился; код выхода — в <see cref="InstallerRun.ExitCode"/>.</summary>
    Exited,

    /// <summary>Пользователь отказал в запросе UAC при самом запуске файла.</summary>
    Cancelled,

    /// <summary>Setup не удалось запустить.</summary>
    Failed,
}

public readonly record struct InstallerRun(InstallerLaunchResult Result, int ExitCode = 0);

/// <summary>Запуск Setup и открытие страницы релиза через оболочку.</summary>
public static class UpdateShell
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: отказ в окне UAC

    /// <summary>
    /// Запускает Setup <b>без повышения прав</b> и ждёт его завершения, не блокируя вызывающий поток. Для «для всех»
    /// (<c>/ALLUSERS</c>) повышение запрашивает сам Setup (<c>PrivilegesRequired=lowest</c>): он перезапускает себя
    /// с UAC, а исходный экземпляр ждёт. Запуск через <c>runas</c> нельзя: из повышенного процесса Inno не умеет
    /// <c>runasoriginaluser</c>, и SaySo после обновления запустился бы с правами администратора.
    /// </summary>
    /// <exception cref="OperationCanceledException">Ожидание отменено (процесс SaySo завершается); Setup продолжает работу.</exception>
    public static async Task<InstallerRun> RunInstallerAsync(string path, string arguments, CancellationToken ct)
    {
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(path, arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path) ?? "",
            });
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return new InstallerRun(InstallerLaunchResult.Cancelled);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            Log.Error("Запуск установщика обновления", e);
            return new InstallerRun(InstallerLaunchResult.Failed);
        }

        if (process is null)
        {
            return new InstallerRun(InstallerLaunchResult.Failed);
        }

        using (process)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(true);
            return new InstallerRun(InstallerLaunchResult.Exited, process.ExitCode);
        }
    }

    /// <summary>
    /// Открывает страницу релиза в браузере; только адреса из белого списка (<see cref="UpdateUrls"/>). Из процесса с правами
    /// администратора — через <c>%WINDIR%\explorer.exe</c>: браузер запускается без повышения (как демон в <c>AdminModeSwitcher</c>).
    /// </summary>
    /// <returns>false — адрес не из белого списка или оболочка не смогла открыть его.</returns>
    public static bool OpenUrl(string url)
    {
        if (!UpdateUrls.IsTrusted(url))
        {
            return false;
        }

        var address = new Uri(url).AbsoluteUri; // в каноническом виде кавычки экранированы: в командную строку попасть не могут
        try
        {
            var info = ProcessIntegrity.IsElevated
                ? new ProcessStartInfo(ShellPaths.Explorer, "\"" + address + "\"") { UseShellExecute = false }
                : new ProcessStartInfo(address) { UseShellExecute = true };
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            Log.Error("Открытие страницы релиза", e);
            return false;
        }
    }
}
