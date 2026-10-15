namespace SaySo.Core;

public enum LaunchMode
{
    Daemon,
    Settings,
    Simulate,
    InstallAdminTask,
    RemoveAdminTask,

    /// <summary>Диагностика: один запрос о последнем релизе без демона, окон и записи состояния.</summary>
    CheckUpdates,

    /// <summary>Диагностика плашки: все состояния по сценарию и замер памяти процесса (для README и проверки освобождения).</summary>
    OverlayDemo,
}

/// <summary>Режим запуска SaySo.exe по аргументам командной строки.</summary>
public sealed record LaunchOptions(LaunchMode Mode, string? WavPath = null, string? OutPath = null, bool Insert = false, int Cycles = 1)
{
    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--settings":
                    return new LaunchOptions(LaunchMode.Settings);
                case "--install-admin-task":
                    return new LaunchOptions(LaunchMode.InstallAdminTask);
                case "--remove-admin-task":
                    return new LaunchOptions(LaunchMode.RemoveAdminTask);
                case "--check-updates":
                    string? report = null;
                    for (var j = i + 1; j + 1 < args.Count; j++)
                    {
                        if (args[j] == "--out")
                        {
                            report = args[j + 1];
                        }
                    }

                    return new LaunchOptions(LaunchMode.CheckUpdates, OutPath: report);
                case "--overlay-demo":
                    string? demoReport = null;
                    var cycles = 1;
                    for (var j = i + 1; j + 1 < args.Count; j++)
                    {
                        if (args[j] == "--out")
                        {
                            demoReport = args[j + 1];
                        }
                        else if (args[j] == "--cycles" && int.TryParse(args[j + 1], out var n) && n is > 0 and <= 100)
                        {
                            cycles = n;
                        }
                    }

                    return new LaunchOptions(LaunchMode.OverlayDemo, OutPath: demoReport, Cycles: cycles);
                case "--simulate":
                    if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("После --simulate нужен путь к WAV-файлу.");
                    }

                    var wav = args[i + 1];
                    string? output = null;
                    var insert = false;
                    for (var j = i + 2; j < args.Count; j++)
                    {
                        if (args[j] == "--out" && j + 1 < args.Count)
                        {
                            output = args[++j];
                        }
                        else if (args[j] == "--insert")
                        {
                            insert = true;
                        }
                    }

                    return new LaunchOptions(LaunchMode.Simulate, wav, output, insert);
            }
        }

        return new LaunchOptions(LaunchMode.Daemon);
    }
}
