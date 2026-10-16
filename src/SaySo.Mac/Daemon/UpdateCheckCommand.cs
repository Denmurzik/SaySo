using System.Text;
using SaySo.Core;
using SaySo.Core.Diagnostics;
using SaySo.Core.Storage;
using SaySo.Core.Updates;

namespace SaySo.Mac.Daemon;

/// <summary>
/// <c>SaySo --check-updates [--out файл]</c>: один запрос о последнем релизе тем же кодом, что у демона, и итог одной строкой.
/// Для проверки AOT-сборки (разбор JSON, HttpClient, версия сборки) без демона и окон; состояние (update-state.json) не пишет.
/// Без <c>--out</c> строка печатается в стандартный вывод (на macOS он виден при запуске из терминала).
/// </summary>
internal static class UpdateCheckCommand
{
    public static int Run(LaunchOptions options)
    {
        var current = VersionTag.Normalize(typeof(UpdateCheckCommand).Assembly.GetName().Version ?? new Version(0, 0, 0));
        string line;
        int code;
        try
        {
            using var http = UpdateHttp.Create();
            var result = new UpdateChecker(http, current).CheckAsync().GetAwaiter().GetResult();
            (line, code) = Describe(current, result);
        }
        catch (Exception e)
        {
            (line, code) = ($"current={current.ToString(3)} outcome=Error {e.GetType().Name}", 3);
        }

        if (options.OutPath is { } path)
        {
            try
            {
                File.WriteAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log.FilePath ??= Path.Combine(AppPaths.Default.Root, "sayso.log");
                Log.Error("--check-updates: не удалось записать отчёт", e);
                return 3;
            }
        }
        else
        {
            Console.WriteLine(line);
        }

        return code;
    }

    private static (string Line, int Code) Describe(Version current, UpdateCheckResult result)
    {
        var prefix = $"current={current.ToString(3)} outcome={result.Outcome}";
        return result.Outcome switch
        {
            UpdateCheckOutcome.Available => ($"{prefix} latest={result.Release!.Version.ToString(3)} installable={result.Release.IsInstallable} setupSize={result.Release.SetupSize}", 0),
            UpdateCheckOutcome.Failed => ($"{prefix} failure={result.Failure}", 2),
            _ => (prefix, 0),
        };
    }
}
