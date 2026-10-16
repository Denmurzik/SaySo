using SaySo.Core;

namespace SaySo.Mac;

public static class Program
{
    public static int Main(string[] args)
    {
        var options = LaunchOptions.Parse(args);

        // Пул на всё время работы: подготовка окон и меню идёт до цикла событий AppKit (у него свои пулы на каждое событие).
        using var pool = Interop.AutoreleasePool.Push();
        return options.Mode switch
        {
            LaunchMode.Settings => Settings.SettingsApp.Run(),
            LaunchMode.Simulate => Daemon.SimulationRunner.Run(options),
            LaunchMode.CheckUpdates => Daemon.UpdateCheckCommand.Run(options),
            LaunchMode.OverlayDemo => Daemon.OverlayDemo.Run(options),
            LaunchMode.InstallAdminTask or LaunchMode.RemoveAdminTask => NotOnMac(),
            _ => Daemon.DaemonHost.Run(),
        };
    }

    // Режим администратора — только Windows (Планировщик заданий, UAC): на macOS вставке он не нужен.
    private static int NotOnMac()
    {
        Console.Error.WriteLine("--install-admin-task и --remove-admin-task есть только в SaySo для Windows.");
        return 2;
    }
}
