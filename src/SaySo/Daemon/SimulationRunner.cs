using System.Buffers;
using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using SaySo.Core;
using SaySo.Core.Audio;
using SaySo.Core.Diagnostics;
using SaySo.Core.Dictation;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Input;
using SaySo.Core.Interop;
using SaySo.Core.Platform;
using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Daemon;

/// <summary>
/// Прогон AOT-конвейера по WAV-файлу (настоящие настройки, ключи DPAPI, движки и резерв) — без микрофона.
/// Аудио подаётся в реальном времени, как с микрофона.
/// </summary>
internal static class SimulationRunner
{
    private const string WindowClass = "SaySo.Simulate";

    /// <summary>
    /// TextInserter рассчитан на поток с циклом сообщений и контекстом синхронизации, возвращающим продолжения на него
    /// (так же устроен демон) — поэтому конвейер крутится под скрытым окном, а не под блокирующим GetResult().
    /// </summary>
    public static int Run(LaunchOptions options)
    {
        Win32SynchronizationContext? sync = null;
        using var window = new MessageWindow(WindowClass, (msg, _, _) =>
        {
            if (msg != DaemonWindow.SyncMessage)
            {
                return null;
            }

            sync?.Drain();
            return 0;
        }, messageOnly: true);
        var context = new Win32SynchronizationContext(window.Handle, DaemonWindow.SyncMessage);
        sync = context;
        SynchronizationContext.SetSynchronizationContext(context);

        var task = RunAsync(options);
        _ = task.ContinueWith(t => context.Post(s => Native.PostQuitMessage(0), null), TaskScheduler.Default);
        MessageLoop.Run();
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // Например, нет WAV-файла или он повреждён: код 1 и JSON с причиной (без текста исключения — там могут быть пути).
            Log.Error("Симуляция", e);
            WriteResult(options.OutPath, null, null, null, $"Сбой симуляции: {e.GetType().Name}", 0, 0, null);
            return 1;
        }
    }

    private static async Task<int> RunAsync(LaunchOptions options)
    {
        var paths = AppPaths.Default;
        paths.EnsureRoot();
        Log.FilePath = Path.Combine(paths.Root, "sayso.log");
        var settings = new SettingsStore(paths).Load();
        var secrets = new SecretStore(paths);
        using var http = SettingsDictationEngines.CreateHttpClient();
        var engines = new SettingsDictationEngines(() => settings, secrets.Load, http, () => new ClientWebSocketTransport());
        var pipeline = new DictationPipeline(engines);
        var pcm = WavReader.ReadPcm(options.WavPath!);

        var clock = Stopwatch.StartNew();
        await using var run = pipeline.Begin();
        run.StartPrimary();
        var chunk = 0;
        for (var i = 0; i < pcm.Length; i += AudioFormat.ChunkBytes)
        {
            run.PushAudio(pcm.AsSpan(i, Math.Min(AudioFormat.ChunkBytes, pcm.Length - i)));
            // Темп по абсолютному дедлайну, а не фиксированной паузе: погрешность таймера не накапливается.
            var wait = (chunk + 1) * (long)AudioFormat.ChunkMilliseconds - clock.ElapsedMilliseconds;
            chunk++;
            if (wait > 0)
            {
                await Task.Delay((int)wait);
            }
        }

        var audioEnd = clock.ElapsedMilliseconds;
        string? text = null, engine = null, primaryError = null, error = null, insert = null;
        var code = 0;
        try
        {
            var result = await run.FinishAsync(CancellationToken.None);
            text = result.Text;
            engine = result.Engine;
            primaryError = result.PrimaryError?.ToString();
        }
        catch (DictationFailedException e)
        {
            error = e.UserMessage;
            primaryError = e.PrimaryError?.ToString();
            code = 1;
        }

        var latency = clock.ElapsedMilliseconds - audioEnd;
        if (options.Insert && !string.IsNullOrWhiteSpace(text))
        {
            await Task.Delay(3000); // время переключиться в целевое окно
            var owner = ClipboardOwner.Create();
            try
            {
                insert = (await new TextInserter(owner, TimeProvider.System).InsertAsync(text, [])).ToString();
            }
            finally
            {
                ClipboardOwner.Destroy(owner);
            }
        }

        WriteResult(options.OutPath, text, engine, primaryError, error, latency, AudioFormat.BytesToMilliseconds(pcm.Length), insert);
        return code;
    }

    private static void WriteResult(string? outPath, string? text, string? engine, string? primaryError, string? error,
        long latencyMs, long audioMs, string? insert)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("text", text);
            w.WriteString("engine", engine);
            w.WriteString("primaryError", primaryError);
            w.WriteString("error", error);
            w.WriteNumber("latencyMs", latencyMs);
            w.WriteNumber("audioMs", audioMs);
            w.WriteString("insert", insert);
            w.WriteEndObject();
        }

        try
        {
            var path = Path.GetFullPath(outPath ?? "sayso-simulate.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, buffer.WrittenSpan.ToArray());
        }
        catch (Exception e)
        {
            // Результат уже получен: недоступный --out не должен менять код возврата и ронять процесс.
            Log.Error("Запись результата симуляции", e);
        }
    }
}
