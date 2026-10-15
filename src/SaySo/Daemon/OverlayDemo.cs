using System.Diagnostics;
using System.Globalization;
using System.Text;
using SaySo.Core;
using SaySo.Core.Engines;
using SaySo.Core.Interop;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;

namespace SaySo.Daemon;

/// <summary>
/// <c>SaySo.exe --overlay-demo [--out файл] [--cycles N]</c>: плашка проходит все состояния тем же кодом, что у демона
/// (запись, обработка, скрытие, ошибка, сообщение, автоскрытие), а в файл пишутся private-память процесса до первого показа,
/// во время показа и после скрытия со сбросом памяти, и отметки времени состояний (Environment.TickCount64 — общие часы
/// системы, по ним сверяется запись экрана для README). Без горячей клавиши, трея и мьютекса демона: идёт рядом с работающим SaySo.
/// </summary>
internal static class OverlayDemo
{
    public static int Run(LaunchOptions options)
    {
        var report = new StringBuilder();
        var started = Stopwatch.StartNew();
        void Line(string text) => report.AppendLine(text);
        void Mark(string state) => Line($"state {state} tick={Environment.TickCount64}");

        using var overlay = new OverlayWindow(() => Speech(started.Elapsed.TotalSeconds));
        var released = 0;
        overlay.Released += () => released++;

        MemoryTrimmer.Trim();
        Pump(1500);
        var baseline = Memory(report, "до первого показа (после Trim)");

        for (var cycle = 1; cycle <= options.Cycles; cycle++)
        {
            Line($"cycle {cycle}");
            Mark("recording");
            overlay.ShowRecording();
            Pump(2200);
            Memory(report, "видна: запись");
            Mark("processing");
            overlay.ShowProcessing();
            Pump(1300);
            Mark("hide");
            overlay.Hide();
            WaitReleased(overlay);
            Pump(700);

            Mark("error");
            overlay.ShowMessage(OverlayMessages.NoFallback(EngineErrorKind.QuotaExceeded).Short, OverlayTone.Error);
            Pump(1600);
            Memory(report, "видна: сообщение");
            Mark("info");
            overlay.ShowMessage(OverlayMessages.FallbackUsed(EngineErrorKind.QuotaExceeded), OverlayTone.Info);
            WaitReleased(overlay); // сообщение скрывается само через 3 с
            Mark("hidden");

            MemoryTrimmer.Trim();
            Pump(1000);
            var after = Memory(report, "после скрытия + Trim");
            Line(string.Create(CultureInfo.InvariantCulture, $"delta_private_mb {(after - baseline) / 1048576.0:+0.00;-0.00}"));
        }

        Line($"released {released}");
        Line($"xaml_modules {Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Count(m => m.ModuleName.StartsWith("Microsoft.UI", StringComparison.OrdinalIgnoreCase))}");
        if (options.OutPath is { } path)
        {
            File.WriteAllText(path, report.ToString(), new UTF8Encoding(false));
        }

        return released == 2 * options.Cycles ? 0 : 2;
    }

    // «Речь» с паузами, как у Probe render-demo.
    private static float Speech(double seconds)
    {
        var i = (float)(seconds * 30);
        return Math.Clamp(0.55f + 0.42f * MathF.Sin(i * 0.7f) * MathF.Sin(i * 0.23f + 1f), 0f, 1f);
    }

    private static unsafe long Memory(StringBuilder report, string label)
    {
        var counters = new Native.PROCESS_MEMORY_COUNTERS_EX { cb = (uint)sizeof(Native.PROCESS_MEMORY_COUNTERS_EX) };
        Native.K32GetProcessMemoryInfo(Native.GetCurrentProcess(), &counters, counters.cb);
        report.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"mem private={counters.PrivateUsage / 1048576.0:F2}MB ws={counters.WorkingSetSize / 1048576.0:F2}MB tick={Environment.TickCount64} {label}"));
        return (long)counters.PrivateUsage;
    }

    private static void WaitReleased(OverlayWindow overlay)
    {
        var until = Environment.TickCount64 + 6000;
        while (overlay.HasScene && Environment.TickCount64 < until)
        {
            Pump(20);
        }
    }

    private static unsafe void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        Native.MSG msg;
        while (Environment.TickCount64 < until)
        {
            while (Native.PeekMessageW(&msg, 0, 0, 0, Native.PM_REMOVE) != 0)
            {
                Native.TranslateMessage(&msg);
                Native.DispatchMessageW(&msg);
            }

            Thread.Sleep(5);
        }
    }
}
