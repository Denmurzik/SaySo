using System.Globalization;
using System.Text.Json;

namespace SaySo.Core.Engines.Gemini;

public enum GeminiEventKind
{
    SetupComplete,
    Interim,
    Final,
    TurnComplete,
    GoAway,
}

public readonly record struct GeminiServerEvent(GeminiEventKind Kind, string? Text = null, TimeSpan? TimeLeft = null);

/// <summary>Разбор сообщений сервера Gemini Live. Неизвестные поля игнорируются.</summary>
public static class GeminiServerParser
{
    public static IReadOnlyList<GeminiServerEvent> Parse(byte[] utf8Json)
    {
        var events = new List<GeminiServerEvent>(2);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Json);
        }
        catch (JsonException)
        {
            return events;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return events;
            }

            if (root.TryGetProperty("setupComplete", out _))
            {
                events.Add(new GeminiServerEvent(GeminiEventKind.SetupComplete));
            }

            if (root.TryGetProperty("serverContent", out var sc) && sc.ValueKind == JsonValueKind.Object)
            {
                ParseServerContent(sc, events);
            }

            if (root.TryGetProperty("goAway", out var goAway))
            {
                TimeSpan? left = null;
                if (goAway.ValueKind == JsonValueKind.Object
                    && goAway.TryGetProperty("timeLeft", out var tl)
                    && tl.ValueKind == JsonValueKind.String)
                {
                    left = ParseDuration(tl.GetString());
                }

                events.Add(new GeminiServerEvent(GeminiEventKind.GoAway, null, left));
            }
        }

        return events;
    }

    private static void ParseServerContent(JsonElement sc, List<GeminiServerEvent> events)
    {
        if (sc.TryGetProperty("interimInputTranscription", out var interim)
            && interim.ValueKind == JsonValueKind.Object
            && interim.TryGetProperty("text", out var it)
            && it.ValueKind == JsonValueKind.String)
        {
            events.Add(new GeminiServerEvent(GeminiEventKind.Interim, it.GetString()));
        }

        var finished = false;
        if (sc.TryGetProperty("inputTranscription", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            if (input.TryGetProperty("text", out var ft) && ft.ValueKind == JsonValueKind.String)
            {
                events.Add(new GeminiServerEvent(GeminiEventKind.Final, ft.GetString()));
            }

            finished = input.TryGetProperty("finished", out var f) && f.ValueKind == JsonValueKind.True;
        }

        var turnComplete = finished
            || IsTrue(sc, "turnComplete")
            || IsTrue(sc, "generationComplete")
            || (sc.TryGetProperty("interactionStatus", out var st)
                && st.ValueKind == JsonValueKind.String
                && st.GetString() == "IDLE");
        if (turnComplete)
        {
            events.Add(new GeminiServerEvent(GeminiEventKind.TurnComplete));
        }
    }

    private static bool IsTrue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // Длительность protobuf JSON: "50s", "29.5s".
    private static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.EndsWith('s'))
        {
            return null;
        }

        return double.TryParse(value.AsSpan(0, value.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(s)
            : null;
    }
}
