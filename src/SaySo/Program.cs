using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SaySo.Core;
using SaySo.Core.Platform;

namespace SaySo;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);
        return options.Mode switch
        {
            LaunchMode.Settings => RunSettings(),
            LaunchMode.InstallAdminTask => InstallAdminTask(),
            LaunchMode.RemoveAdminTask => RemoveAdminTask(),
            LaunchMode.Simulate => Daemon.SimulationRunner.Run(options),
            // Демон никогда не касается типов XAML — иначе в процесс загрузится Microsoft.UI.Xaml.dll.
            _ => Daemon.DaemonHost.Run(),
        };
    }

    private static int InstallAdminTask()
    {
        var exe = Environment.ProcessPath!;
        new AutostartManager().Set(false, exe); // автозапуск переходит к Планировщику
        return AdminTask.Install(exe);
    }

    private static int RemoveAdminTask() =>
        AdminTask.Exists() ? AdminTask.Remove() : 0; // задачи уже нет — цель достигнута, schtasks /Delete вернул бы ошибку

    private static int RunSettings()
    {
        Mutex? mutex = null;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, @"Local\SaySo.Settings", out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            createdNew = false; // мьютекс создан элевированным экземпляром окна настроек: окно уже открыто
        }

        try
        {
            if (!createdNew)
            {
                ActivateExistingSettingsWindow();
                return 0;
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(callbackParams =>
            {
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new Settings.App();
            });
            return 0;
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    private static void ActivateExistingSettingsWindow()
    {
        var existing = Core.Interop.Native.FindWindowExW(0, 0, Settings.SettingsWindowInfo.WindowClass, Settings.SettingsWindowInfo.Title);
        if (existing == 0)
        {
            return;
        }

        if (Core.Interop.Native.IsIconic(existing))
        {
            Core.Interop.Native.ShowWindow(existing, Core.Interop.Native.SW_RESTORE);
        }

        Core.Interop.Native.SetForegroundWindow(existing);
    }
}
