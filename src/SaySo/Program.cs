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
            LaunchMode.RemoveAdminTask => AdminTask.Remove(),
            LaunchMode.Simulate => 2, // задача 16
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

    private static int RunSettings()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SaySo.Settings", out var createdNew);
        if (!createdNew)
        {
            var existing = Core.Interop.Native.FindWindowExW(0, 0, null, "SaySo — настройки");
            if (existing != 0)
            {
                Core.Interop.Native.SetForegroundWindow(existing);
            }

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
}
