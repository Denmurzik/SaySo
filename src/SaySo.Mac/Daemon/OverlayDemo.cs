using System.Globalization;
using System.Text;
using SaySo.Core;
using SaySo.Core.Engines;
using SaySo.Core.Overlay;
using SaySo.Mac.Interop;
using SaySo.Mac.Overlay;
using SaySo.Mac.Platform;

namespace SaySo.Mac.Daemon;

/// <summary>
/// <c>SaySo --overlay-demo [--out файл] [--cycles N]</c>: плашка проходит все состояния тем же кодом, что у демона
/// (запись, обработка, скрытие, ошибка, сообщение, автоскрытие), а в файл пишется физическая память процесса до первого
/// показа, во время показа и после скрытия со сбросом памяти. Без горячей клавиши, значка и блокировки единственного
/// экземпляра: идёт рядом с работающим SaySo.
/// </summary>
internal static class OverlayDemo
{
    public static int Run(LaunchOptions options)
    {
        AppHost.Initialize(NS.ActivationAccessory);
        var report = new StringBuilder();
        var started = Environment.TickCount64;
        var overlay = new OverlayPanel(() => Speech((Environment.TickCount64 - started) / 1000.0));
        var released = 0;
        overlay.Released += () => released++;
        var code = 2;
        _ = RunAsync();
        AppHost.Run();
        overlay.Dispose();
        if (options.OutPath is { } path)
        {
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        }

        return code;

        async Task RunAsync()
        {
            try
            {
                MemoryTrimmer.Trim();
                await Task.Delay(1500);
                var baseline = Memory(report, "до первого показа (после Trim)");
                for (var cycle = 1; cycle <= options.Cycles; cycle++)
                {
                    report.AppendLine($"cycle {cycle}");
                    Mark(report, "recording");
                    overlay.ShowRecording();
                    await Task.Delay(2200);
                    Memory(report, "видна: запись");
                    Mark(report, "processing");
                    overlay.ShowProcessing();
                    await Task.Delay(1300);
                    Mark(report, "hide");
                    overlay.Hide();
                    await WaitReleased(overlay);
                    await Task.Delay(700);

                    Mark(report, "error");
                    overlay.ShowMessage(OverlayMessages.NoFallback(EngineErrorKind.QuotaExceeded).Short, OverlayTone.Error);
                    await Task.Delay(1600);
                    Memory(report, "видна: сообщение");
                    Mark(report, "info");
                    overlay.ShowMessage(OverlayMessages.FallbackUsed(EngineErrorKind.QuotaExceeded), OverlayTone.Info);
                    await WaitReleased(overlay); // сообщение скрывается само через 3 с
                    Mark(report, "hidden");

                    MemoryTrimmer.Trim();
                    await Task.Delay(1000);
                    var after = Memory(report, "после скрытия + Trim");
                    report.AppendLine(string.Create(CultureInfo.InvariantCulture, $"delta_footprint_mb {(after - baseline) / 1048576.0:+0.00;-0.00}"));
                }

                report.AppendLine($"released {released}");
                code = released == 2 * options.Cycles ? 0 : 2;
            }
            finally
            {
                AppHost.Stop();
            }
        }
    }

    private static void Mark(StringBuilder report, string state) => report.AppendLine($"state {state} tick={Environment.TickCount64}");

    // «Речь» с паузами, как у Probe render-demo.
    private static float Speech(double seconds)
    {
        var i = (float)(seconds * 30);
        return Math.Clamp(0.55f + 0.42f * MathF.Sin(i * 0.7f) * MathF.Sin(i * 0.23f + 1f), 0f, 1f);
    }

    private static long Memory(StringBuilder report, string label)
    {
        var footprint = MemoryTrimmer.Footprint();
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"mem footprint={footprint / 1048576.0:F2}MB tick={Environment.TickCount64} {label}"));
        return footprint;
    }

    private static async Task WaitReleased(OverlayPanel overlay)
    {
        var until = Environment.TickCount64 + 6000;
        while (overlay.HasScene && Environment.TickCount64 < until)
        {
            await Task.Delay(20);
        }
    }
}
