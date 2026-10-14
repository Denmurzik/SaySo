namespace SaySo.Core;

public enum LaunchMode
{
    Daemon,
    Settings,
    Simulate,
    InstallAdminTask,
    RemoveAdminTask,
}

/// <summary>Режим запуска SaySo.exe по аргументам командной строки.</summary>
public sealed record LaunchOptions(LaunchMode Mode, string? WavPath = null, string? OutPath = null, bool Insert = false)
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
