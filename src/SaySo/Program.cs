using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SaySo.Core;

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
            _ => RunDaemon(),
        };
    }

    // Демон никогда не касается типов XAML — иначе в процесс загрузится Microsoft.UI.Xaml.dll.
    private static int RunDaemon()
    {
        // Заглушка до задачи 13: процесс просто живёт (проверка, что XAML не загружается).
        Thread.Sleep(Timeout.Infinite);
        return 0;
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
