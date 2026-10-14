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

    /// <param name="enable">Новый режим.</param>
    /// <param name="persist">
    /// Записать новый режим в настройки; вызывается после успешной установки/снятия задачи и до перезапуска демона
    /// (демон при старте читает настройки). false — записать не удалось.
    /// </param>
    /// <returns>Текст ошибки или null. Если ошибка пришла после <paramref name="persist"/>, режим уже сохранён.</returns>
    public static async Task<string?> SetAsync(bool enable, Func<bool> persist)
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

        if (!persist())
        {
            return SettingsModel.SaveFailedMessage;
        }

        IpcClient.Send(IpcProtocol.Quit);
        for (var i = 0; i < 50 && IpcClient.IsDaemonRunning(); i++)
        {
            await Task.Delay(100);
        }

        if (IpcClient.IsDaemonRunning())
        {
            return "Режим переключён, но SaySo не закрылся. Выберите «Выход» в меню значка в трее и запустите SaySo снова.";
        }

        if (enable)
        {
            var code = AdminTask.Run();
            if (code != 0)
            {
                return $"Режим переключён, но задача Планировщика не запустилась (код {code}). Запустите SaySo вручную.";
            }
        }
        else
        {
            // Через Проводник — чтобы демон запустился без прав администратора, даже если окно настроек элевировано.
            using var explorer = Process.Start(new ProcessStartInfo("explorer.exe", "\"" + exe + "\"") { UseShellExecute = false });
        }

        return null;
    }
}
