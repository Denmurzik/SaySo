using System.Net;
using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Engines.Groq;
using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Core.Dictation;

/// <summary>
/// Движки из текущих настроек и ключей. Кольцо ключей Gemini — одно на всё время жизни объекта:
/// паузы ключей (квота, недействительный ключ) переживают отдельные реплики.
/// </summary>
public sealed class SettingsDictationEngines(
    Func<AppSettings> settings,
    Func<Secrets> secrets,
    HttpClient http,
    Func<IWebSocketTransport> transportFactory,
    TimeProvider? time = null) : IDictationEngines
{
    private readonly GeminiKeyRing _keys = new(time ?? TimeProvider.System);

    public IStreamingEngine? Primary
    {
        get
        {
            _keys.SetKeys(secrets().GeminiApiKeys);
            return new GeminiLiveEngine(transportFactory, _keys, BuildConnection, CompletionTimings.Default);
        }
    }

    public IBatchEngine? Fallback
    {
        get
        {
            var s = settings();
            var r = s.Recognition;
            var key = secrets().GroqApiKey;
            if (!r.GroqFallbackEnabled || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return new GroqFallbackEngine(
                new GroqClient(http, r.GroqBaseUrl, key),
                new GroqFallbackOptions(
                    r.GroqWhisperModel,
                    GroqClient.ToWhisperLanguage(r.LanguageCodes),
                    GroqClient.BuildVocabularyPrompt(s.Vocabulary),
                    r.GroqCleanupEnabled,
                    r.GroqCleanupModel));
        }
    }

    /// <summary>HttpClient без простаивающих соединений дольше 30 с (в фоне — ноль сокетов).</summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler
    {
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // таймауты — на уровне каждого запроса
    };

    private GeminiConnection BuildConnection(string key)
    {
        var s = settings();
        var r = s.Recognition;
        return new GeminiConnection(new Uri(r.GeminiEndpoint), key,
            new GeminiSessionOptions(r.GeminiModel, r.Mode, r.LanguageCodes, s.Vocabulary));
    }
}
