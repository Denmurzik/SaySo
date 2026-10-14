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
            LaunchMode.InstallAdminTask => AdminTask.Install(Environment.ProcessPath!),
            LaunchMode.RemoveAdminTask => AdminTask.Remove(),
            LaunchMode.Simulate => 2, // задача 16
            // Демон никогда не касается типов XAML — иначе в процесс загрузится Microsoft.UI.Xaml.dll.
            _ => Daemon.DaemonHost.Run(),
        };
    }

    private static int RunSettings()
    {
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
