using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using SaySo.Core.Settings;

namespace SaySo.Core.Engines.Gemini;

/// <summary>Клиентские сообщения Gemini Live (UTF-8 JSON, без рефлексии).</summary>
public static class GeminiMessages
{
    // Кириллица пишется как есть, без \uXXXX — меньше трафика и читаемее в Probe.
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] BuildSetup(GeminiSessionOptions o)
    {
        return Write(w =>
        {
            w.WriteStartObject();
            w.WriteStartObject("setup");
            w.WriteString("model", o.Model.StartsWith("models/", StringComparison.Ordinal) ? o.Model : "models/" + o.Model);
            w.WriteStartObject("generationConfig");
            w.WriteStartArray("responseModalities");
            w.WriteStringValue("TEXT");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteStartObject("inputAudioTranscription");
            w.WriteString("mode", o.Mode == TranscriptionMode.Smart ? "SMART" : "VERBATIM");
            w.WriteStartArray("languageCodes");
            foreach (var code in o.LanguageCodes)
            {
                w.WriteStringValue(code);
            }

            w.WriteEndArray();
            if (o.Vocabulary.Count > 0)
            {
                w.WriteStartArray("customVocabulary");
                foreach (var term in o.Vocabulary)
                {
                    w.WriteStringValue(term);
                }

                w.WriteEndArray();
            }

            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        });
    }

    public static byte[] BuildAudio(ReadOnlySpan<byte> pcm)
    {
        var buffer = new ArrayBufferWriter<byte>(pcm.Length * 4 / 3 + 128);
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            w.WriteStartObject();
            w.WriteStartObject("realtimeInput");
            w.WriteStartObject("audio");
            w.WriteBase64String("data", pcm);
            w.WriteString("mimeType", "audio/pcm;rate=16000");
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] BuildAudioStreamEnd() => Write(w =>
    {
        w.WriteStartObject();
        w.WriteStartObject("realtimeInput");
        w.WriteBoolean("audioStreamEnd", true);
        w.WriteEndObject();
        w.WriteEndObject();
    });

    private static byte[] Write(Action<Utf8JsonWriter> body)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer, Options))
        {
            body(w);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
