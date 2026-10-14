namespace SaySo.Core.Engines.Groq;

public sealed record GroqFallbackOptions(
    string WhisperModel,
    string? Language,
    string? VocabularyPrompt,
    bool CleanupEnabled,
    string CleanupModel);

/// <summary>Резерв: Whisper на Groq, затем LLM-очистка (при её сбое — сырой текст Whisper).</summary>
public sealed class GroqFallbackEngine(GroqClient client, GroqFallbackOptions options) : IBatchEngine
{
    public string Name => "groq";

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        var raw = WhisperOutputFilter.Clean(await client
            .TranscribeAsync(wav, options.WhisperModel, options.Language, options.VocabularyPrompt, ct)
            .ConfigureAwait(false));
        if (raw.Length == 0 || !options.CleanupEnabled)
        {
            return raw;
        }

        try
        {
            var cleaned = await client.CleanupAsync(raw, options.CleanupModel, ct).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(cleaned) ? raw : cleaned;
        }
        catch (TranscriptionException)
        {
            return raw;
        }
    }
}
