using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using SaySo.Core.Audio;
using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Engines.Groq;
using SaySo.Core.Overlay;
using SaySo.Core.Settings;
using SaySo.Probe;

Console.OutputEncoding = Encoding.UTF8;
var opts = Args.Parse(args);
try
{
    switch (opts.Command)
    {
        case "gemini":
            await RawGemini(opts, opts.Get("model") ?? RecognitionSettings.DefaultGeminiModel);
            return 0;
        case "session":
            await SessionGemini(opts);
            return 0;
        case "groq":
            await GroqProbe(opts);
            return 0;
        case "errors":
            await ErrorScenarios(opts);
            return 0;
        case "render-demo":
            RenderDemo(opts.Get("out") ?? "docs/assets/overlay-demo.png");
            return 0;
        case "render-states":
            RenderStates(opts.Get("out-dir") ?? "docs/assets");
            return 0;
        case "overlay-capture":
            return OverlayCapture.Run(opts.Require("exe"), opts.Get("out-dir") ?? "artifacts/overlay-capture");
        default:
            Console.WriteLine("Команды: overlay-capture --exe SaySo.exe [--out-dir D] | render-demo [--out F] | render-states [--out-dir D] | gemini --wav F [--realtime] [--mode smart|verbatim] [--lang ru-RU] [--vocab \"a,b\"] [--model M] [--query-key] | session --wav F [--realtime] | groq --wav F [--no-cleanup] | errors");
            return 2;
    }
}
catch (Exception e)
{
    Console.WriteLine($"ОШИБКА: {e.GetType().Name}: {e.Message}");
    return 1;
}

static GeminiSessionOptions SessionOptions(Args o, string model) => new(
    model,
    o.Get("mode") == "verbatim" ? TranscriptionMode.Verbatim : TranscriptionMode.Smart,
    o.Get("lang")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
    o.Get("vocab")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);

static string RequireGeminiKey() => ProbeKeys.Gemini() ?? throw new InvalidOperationException("Нет ключа Gemini: GEMINI_API_KEY или tools/set-keys.ps1.");

static async Task RawGemini(Args o, string model)
{
    var pcm = WavReader.ReadPcm(o.Require("wav"));
    var key = RequireGeminiKey();
    var endpoint = RecognitionSettings.DefaultGeminiEndpoint;
    using var ws = new ClientWebSocket();
    Uri uri;
    if (o.Has("query-key"))
    {
        uri = new Uri(endpoint + "?key=" + Uri.EscapeDataString(key));
    }
    else
    {
        uri = new Uri(endpoint);
        ws.Options.SetRequestHeader("x-goog-api-key", key);
    }

    ws.Options.CollectHttpResponseDetails = true;
    var clock = Stopwatch.StartNew();
    try
    {
        await ws.ConnectAsync(uri, CancellationToken.None);
    }
    catch (WebSocketException e)
    {
        Console.WriteLine($"Рукопожатие не удалось: HTTP {(int)ws.HttpStatusCode}, {e.Message}");
        return;
    }

    Console.WriteLine($"[{clock.ElapsedMilliseconds,6} мс] подключено ({(o.Has("query-key") ? "?key=" : "заголовок x-goog-api-key")})");
    var setup = GeminiMessages.BuildSetup(SessionOptions(o, model));
    Console.WriteLine("→ " + Encoding.UTF8.GetString(setup));
    await ws.SendAsync(setup, WebSocketMessageType.Text, true, CancellationToken.None);

    long endAt = -1;
    var receiver = Task.Run(async () =>
    {
        var buffer = new byte[1 << 20];
        while (ws.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            var count = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buffer.AsMemory(count), CancellationToken.None);
                count += r.Count;
            }
            while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);

            var rel = endAt < 0 ? $"до конца потока, t={clock.ElapsedMilliseconds}" : $"+{clock.ElapsedMilliseconds - endAt} мс после конца";
            if (r.MessageType == WebSocketMessageType.Close)
            {
                Console.WriteLine($"[{rel}] ЗАКРЫТИЕ: {(int?)ws.CloseStatus} «{ws.CloseStatusDescription}»");
                return;
            }

            Console.WriteLine($"[{rel}] ← {r.MessageType} {count} байт: {Encoding.UTF8.GetString(buffer, 0, count)}");
        }
    });

    // Ждём setupComplete не дольше 10 с (печатает приёмник).
    await Task.Delay(1500);
    var realtime = o.Has("realtime");
    for (var i = 0; i < pcm.Length; i += AudioFormat.ChunkBytes)
    {
        var chunk = pcm.AsSpan(i, Math.Min(AudioFormat.ChunkBytes, pcm.Length - i)).ToArray();
        await ws.SendAsync(GeminiMessages.BuildAudio(chunk), WebSocketMessageType.Text, true, CancellationToken.None);
        if (realtime)
        {
            await Task.Delay(AudioFormat.ChunkMilliseconds);
        }
    }

    await ws.SendAsync(GeminiMessages.BuildAudioStreamEnd(), WebSocketMessageType.Text, true, CancellationToken.None);
    endAt = clock.ElapsedMilliseconds;
    Console.WriteLine($"→ audioStreamEnd (аудио {AudioFormat.BytesToMilliseconds(pcm.Length)} мс)");
    await Task.WhenAny(receiver, Task.Delay(10_000));
    if (ws.State == WebSocketState.Open)
    {
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await Task.WhenAny(receiver, Task.Delay(2000));
    }
}

static async Task SessionGemini(Args o)
{
    var pcm = WavReader.ReadPcm(o.Require("wav"));
    var conn = new GeminiConnection(new Uri(RecognitionSettings.DefaultGeminiEndpoint), RequireGeminiKey(),
        SessionOptions(o, o.Get("model") ?? RecognitionSettings.DefaultGeminiModel));
    await using var session = new GeminiLiveSession(new ClientWebSocketTransport(), conn, CompletionTimings.Default);
    session.InterimText += t => Console.WriteLine("  … " + t);
    session.Start();
    for (var i = 0; i < pcm.Length; i += AudioFormat.ChunkBytes)
    {
        session.PushAudio(pcm.AsMemory(i, Math.Min(AudioFormat.ChunkBytes, pcm.Length - i)));
        if (o.Has("realtime"))
        {
            await Task.Delay(AudioFormat.ChunkMilliseconds);
        }
    }

    var sw = Stopwatch.StartNew();
    var text = await session.CompleteAsync(CancellationToken.None);
    Console.WriteLine($"ИТОГ ({sw.ElapsedMilliseconds} мс после конца аудио): {text}");
}

static async Task GroqProbe(Args o)
{
    var wav = File.ReadAllBytes(o.Require("wav"));
    var key = ProbeKeys.Groq() ?? throw new InvalidOperationException("Нет ключа Groq: GROQ_API_KEY или tools/set-keys.ps1.");
    using var http = new HttpClient();
    var client = new GroqClient(http, RecognitionSettings.DefaultGroqBaseUrl, key);
    var sw = Stopwatch.StartNew();
    var raw = await client.TranscribeAsync(wav, RecognitionSettings.DefaultGroqWhisperModel, o.Get("lang"), null, CancellationToken.None);
    Console.WriteLine($"Whisper ({sw.ElapsedMilliseconds} мс): {raw}");
    if (o.Has("no-cleanup"))
    {
        return;
    }

    sw.Restart();
    var cleaned = await client.CleanupAsync(WhisperOutputFilter.Clean(raw), RecognitionSettings.DefaultGroqCleanupModel, CancellationToken.None);
    Console.WriteLine($"Очистка ({sw.ElapsedMilliseconds} мс): {cleaned}");
}

static async Task ErrorScenarios(Args o)
{
    var silence = new byte[AudioFormat.ChunkBytes];
    foreach (var (title, key, model) in new[]
    {
        ("Неверный ключ", "AIzaSy-INVALID-KEY-FOR-PROBE", RecognitionSettings.DefaultGeminiModel),
        ("Несуществующая модель", RequireGeminiKey(), "gemini-0.0-no-such-model"),
    })
    {
        Console.WriteLine($"== {title}");
        var conn = new GeminiConnection(new Uri(RecognitionSettings.DefaultGeminiEndpoint), key,
            new GeminiSessionOptions(model, TranscriptionMode.Smart, [], []));
        await using var session = new GeminiLiveSession(new ClientWebSocketTransport(), conn, CompletionTimings.Default);
        session.Start();
        session.PushAudio(silence);
        try
        {
            Console.WriteLine("   неожиданно успешно: «" + await session.CompleteAsync(CancellationToken.None) + "»");
        }
        catch (TranscriptionException e)
        {
            Console.WriteLine($"   {e.Kind}: {e.Message}");
        }
    }
}

static void RenderDemo(string path)
{
    const float scale = 2f;
    var levels = new float[OverlayRenderer.BarCount];
    var (w, h) = OverlayRenderer.Measure(new OverlayFrame(OverlayMode.Recording, levels, 0), scale);
    var frames = new List<uint[]>();
    uint[] Draw(OverlayFrame f)
    {
        var px = new uint[w * h];
        OverlayRenderer.Render(px, w, h, f, scale);
        return px;
    }

    for (var i = 0; i < 54; i++) // ~1,8 с записи: «речь» с паузами
    {
        var speech = i < 4 ? 0.08f : Math.Clamp(0.55f + 0.42f * MathF.Sin(i * 0.7f) * MathF.Sin(i * 0.23f + 1f), 0f, 1f);
        Array.Copy(levels, 1, levels, 0, levels.Length - 1);
        levels[^1] = speech;
        frames.Add(Draw(new OverlayFrame(OverlayMode.Recording, levels.ToArray(), i * 33L)));
    }

    for (var i = 0; i < 30; i++) // ~1 с обработки
    {
        frames.Add(Draw(new OverlayFrame(OverlayMode.Processing, [], i * 33L)));
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    ApngWriter.Write(path, w, h, frames, 33);
    Console.WriteLine($"OK {path} ({w}×{h}, кадров: {frames.Count})");
}

/// <summary>Неподвижные состояния оверлея (запись, обработка, ошибка, сообщение) — для README; тот же рендерер и те же тексты, что у демона.</summary>
static void RenderStates(string dir)
{
    const float scale = 2f;
    Directory.CreateDirectory(Path.GetFullPath(dir));

    OverlayFrame Message(string text, OverlayTone tone)
    {
        var mask = GdiTextMask.RenderText(text, (int)MathF.Round(13 * scale), (int)MathF.Round(400 * scale)); // та же ширина текста, что у плашки
        if (mask.Width == 0 || mask.Height == 0)
        {
            // GdiTextMask при сбое GDI молча возвращает пустую маску — на картинке была бы пилюля без текста.
            throw new InvalidOperationException($"GDI не отрисовал текст «{text}» (пустая маска).");
        }

        return new OverlayFrame(OverlayMode.Message, [], 0, tone, mask);
    }

    var frames = new (string File, OverlayFrame Frame)[]
    {
        ("overlay-recording.png", new OverlayFrame(OverlayMode.Recording, [0.18f, 0.42f, 0.78f, 1f, 0.66f, 0.34f, 0.2f], 0)),
        ("overlay-processing.png", new OverlayFrame(OverlayMode.Processing, [], 400)),
        ("overlay-error.png", Message(OverlayMessages.NoFallback(EngineErrorKind.QuotaExceeded).Short, OverlayTone.Error)),
        ("overlay-info.png", Message(OverlayMessages.FallbackUsed(EngineErrorKind.QuotaExceeded), OverlayTone.Info)),
    };

    foreach (var (file, frame) in frames)
    {
        var (w, h) = OverlayRenderer.Measure(frame, scale);
        var px = new uint[w * h];
        OverlayRenderer.Render(px, w, h, frame, scale);
        var path = Path.Combine(dir, file);
        ApngWriter.WriteStill(path, w, h, px);
        Console.WriteLine($"OK {path} ({w}×{h})");
    }
}

/// <summary>Простейший разбор аргументов: команда + --ключ [значение].</summary>
internal sealed class Args
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

    public string Command { get; private init; } = "";

    public static Args Parse(string[] argv)
    {
        var a = new Args { Command = argv.Length > 0 ? argv[0] : "" };
        for (var i = 1; i < argv.Length; i++)
        {
            if (!argv[i].StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var name = argv[i][2..];
            var hasValue = i + 1 < argv.Length && !argv[i + 1].StartsWith("--", StringComparison.Ordinal);
            a._values[name] = hasValue ? argv[++i] : null;
        }

        return a;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Get(string name) => _values.GetValueOrDefault(name);

    public string Require(string name) => Get(name) ?? throw new ArgumentException($"Нужен параметр --{name}.");
}
