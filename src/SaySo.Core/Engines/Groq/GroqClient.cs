using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SaySo.Core.Engines.Groq;

/// <summary>Клиент Groq (OpenAI-совместимый API): Whisper и chat completions. Ключ не попадает в сообщения ошибок.</summary>
public sealed class GroqClient(HttpClient http, string baseUrl, string apiKey)
{
    /// <summary>~224 токена Whisper с запасом для кириллицы.</summary>
    public const int MaxPromptChars = 400;

    private static readonly TimeSpan TranscribeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _base = baseUrl.TrimEnd('/');

    public static string? ToWhisperLanguage(IReadOnlyList<string> languageCodes) =>
        languageCodes.Count == 1 ? languageCodes[0].Split('-')[0].ToLowerInvariant() : null;

    public static string? BuildVocabularyPrompt(IReadOnlyList<string> vocabulary)
    {
        if (vocabulary.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        foreach (var term in vocabulary)
        {
            var addition = sb.Length == 0 ? term : ", " + term;
            if (sb.Length + addition.Length > MaxPromptChars)
            {
                break;
            }

            sb.Append(addition);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    public async Task<string> TranscribeAsync(byte[] wav, string model, string? language, string? prompt, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "audio.wav");
        form.Add(new StringContent(model), "model");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0"), "temperature");
        if (!string.IsNullOrEmpty(language))
        {
            form.Add(new StringContent(language), "language");
        }

        if (!string.IsNullOrEmpty(prompt))
        {
            form.Add(new StringContent(prompt), "prompt");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/audio/transcriptions") { Content = form };
        using var doc = await SendAsync(request, TranscribeTimeout, ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()!.Trim()
            : throw new TranscriptionException(EngineErrorKind.Protocol, "Groq: в ответе нет поля text.");
    }

    public async Task<string> CleanupAsync(string text, string model, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteStartArray("messages");
            WriteMessage(w, "system", CleanupPrompt.System);
            WriteMessage(w, "user", text);
            w.WriteEndArray();
            w.WriteNumber("temperature", 0);
            w.WriteString("reasoning_effort", "low");
            w.WriteBoolean("include_reasoning", false);
            // Рассуждение тратит тот же бюджет — берём с запасом относительно длины текста.
            w.WriteNumber("max_completion_tokens", Math.Clamp(1024 + text.Length, 1024, 4096));
            w.WriteBoolean("stream", false);
            w.WriteEndObject();
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _base + "/chat/completions")
        {
            Content = new ByteArrayContent(buffer.WrittenSpan.ToArray())
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        using var doc = await SendAsync(request, CleanupTimeout, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0
            && choices[0].TryGetProperty("message", out var message)
            && message.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.String)
        {
            return content.GetString()!.Trim();
        }

        throw new TranscriptionException(EngineErrorKind.Protocol, "Groq: в ответе нет choices[0].message.content.");
    }

    public async Task ValidateKeyAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _base + "/models");
        using var _ = await SendAsync(request, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
    }

    private static void WriteMessage(Utf8JsonWriter w, string role, string content)
    {
        w.WriteStartObject();
        w.WriteString("role", role);
        w.WriteString("content", content);
        w.WriteEndObject();
    }

    private async Task<JsonDocument> SendAsync(HttpRequestMessage request, TimeSpan timeout, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TranscriptionException(EngineErrorKind.Timeout, "Groq не ответил вовремя.");
        }
        catch (HttpRequestException e)
        {
            throw new TranscriptionException(EngineErrorKind.Network, "Нет связи с Groq.", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new TranscriptionException(Classify(response.StatusCode),
                    $"Groq вернул HTTP {(int)response.StatusCode}: {ErrorMessage(body)}");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException e)
            {
                throw new TranscriptionException(EngineErrorKind.Protocol, "Groq вернул некорректный JSON.", e);
            }
        }
    }

    private static EngineErrorKind Classify(HttpStatusCode status) => (int)status switch
    {
        429 => EngineErrorKind.QuotaExceeded,
        401 or 403 => EngineErrorKind.AuthFailed,
        404 => EngineErrorKind.ModelNotFound,
        >= 500 => EngineErrorKind.Network,
        _ => EngineErrorKind.Protocol,
    };

    private static string ErrorMessage(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)
                && e.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                return m.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Тело не JSON — сообщим только код.
        }

        return "без описания";
    }
}
