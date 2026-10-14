using SaySo.Core.Storage;

namespace SaySo.Probe;

/// <summary>Ключи для Probe: переменные окружения, иначе DPAPI-хранилище SaySo.</summary>
internal static class ProbeKeys
{
    public static string? Gemini() =>
        Environment.GetEnvironmentVariable("GEMINI_API_KEY") is { Length: > 0 } k ? k : new SecretStore(AppPaths.Default).Load().GeminiApiKeys.FirstOrDefault();

    public static string? Groq() =>
        Environment.GetEnvironmentVariable("GROQ_API_KEY") is { Length: > 0 } k ? k : new SecretStore(AppPaths.Default).Load().GroqApiKey;
}
