using SaySo.Core.Diagnostics;

namespace SaySo.Core.Engines.Gemini;

/// <summary>Фабрика сессий Gemini Live: ключ берётся из кольца, ошибки сессии возвращаются кольцу.</summary>
public sealed class GeminiLiveEngine(
    Func<IWebSocketTransport> transportFactory,
    GeminiKeyRing keys,
    Func<string, GeminiConnection> connectionForKey,
    CompletionTimings timings) : IStreamingEngine
{
    public IStreamingSession StartSession()
    {
        if (keys.Count == 0)
        {
            throw new TranscriptionException(EngineErrorKind.NotConfigured, "Не задан ключ Gemini.");
        }

        var key = keys.Acquire()
            ?? throw new TranscriptionException(EngineErrorKind.QuotaExceeded, "Все ключи Gemini временно на паузе (лимит или недействительный ключ).");
        var session = new GeminiLiveSession(transportFactory(), connectionForKey(key), timings);
        session.Failed += e =>
        {
            keys.ReportFailure(key, e.Kind);
            Log.Info($"Gemini: ключ №{keys.NumberOf(key)} — {e.Kind}");
        };
        session.Start();
        return session;
    }
}
