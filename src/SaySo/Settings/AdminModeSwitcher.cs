using System.ComponentModel;
using System.Diagnostics;
using SaySo.Core.Platform;

namespace SaySo.Settings;

/// <summary>
/// Включение/выключение режима «от администратора»: задача Планировщика ставится/снимается элевированным
/// экземпляром SaySo.exe (запрос UAC), затем демон перезапускается в нужном режиме.
/// </summary>
internal static class AdminModeSwitcher
{
    private const int ErrorCancelled = 1223;

    public static async Task<string?> SetAsync(bool enable)
    {
        var exe = Environment.ProcessPath!;
        int exitCode;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, enable ? "--install-admin-task" : "--remove-admin-task")
            {
                UseShellExecute = true,
                Verb = "runas",
            })!;
            await process.WaitForExitAsync();
            exitCode = process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return "Действие отменено в окне контроля учётных записей.";
        }

        if (exitCode != 0)
        {
            return $"Планировщик заданий вернул код {exitCode}.";
        }

        IpcClient.Send(IpcProtocol.Quit);
        for (var i = 0; i < 50 && IpcClient.IsDaemonRunning(); i++)
        {
            await Task.Delay(100);
        }

        if (enable)
        {
            AdminTask.Run();
        }
        else
        {
            // Через Проводник — чтобы демон запустился без прав администратора, даже если окно настроек элевировано.
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false });
        }

        return null;
    }
}
