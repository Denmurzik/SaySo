using SaySo.Core.Audio;

namespace SaySo.Core.Settings;

/// <summary>Приводит настройки к допустимым значениям (в т.ч. после ручной правки JSON).</summary>
public static class SettingsNormalizer
{
    public const int MinTapThresholdMs = 150;
    public const int MaxTapThresholdMs = 1000;
    public const int MaxVocabularyTerms = 1000;
    public const int MaxDeviceNameLength = 256;

    public static AppSettings Normalize(AppSettings s)
    {
        s.Hotkey ??= new HotkeySettings();
        s.Recognition ??= new RecognitionSettings();
        s.Vocabulary ??= [];

        s.Hotkey.Keys = (s.Hotkey.Keys ?? []).Where(k => k is > 0 and < 255).Distinct().ToList();
        if (s.Hotkey.Keys.Count == 0)
        {
            s.Hotkey.Keys = HotkeySettings.DefaultKeys();
        }

        s.Hotkey.TapThresholdMs = Math.Clamp(s.Hotkey.TapThresholdMs, MinTapThresholdMs, MaxTapThresholdMs);
        s.SoundTheme = SoundThemes.Normalize(s.SoundTheme);

        var r = s.Recognition;
        r.GeminiModel = OrDefault(r.GeminiModel, RecognitionSettings.DefaultGeminiModel);
        r.GeminiEndpoint = OrDefault(r.GeminiEndpoint, RecognitionSettings.DefaultGeminiEndpoint);
        r.GroqBaseUrl = OrDefault(r.GroqBaseUrl, RecognitionSettings.DefaultGroqBaseUrl);
        r.GroqWhisperModel = OrDefault(r.GroqWhisperModel, RecognitionSettings.DefaultGroqWhisperModel);
        r.GroqCleanupModel = OrDefault(r.GroqCleanupModel, RecognitionSettings.DefaultGroqCleanupModel);
        r.LanguageCodes = CleanList(r.LanguageCodes, int.MaxValue);

        s.Vocabulary = CleanList(s.Vocabulary, MaxVocabularyTerms);
        if (string.IsNullOrWhiteSpace(s.MicrophoneDeviceId))
        {
            s.MicrophoneDeviceId = null;
        }

        // Имя без идентификатора бессмысленно: «по умолчанию» очищает оба значения.
        s.MicrophoneDeviceName = s.MicrophoneDeviceId is null || string.IsNullOrWhiteSpace(s.MicrophoneDeviceName)
            ? null
            : Truncate(s.MicrophoneDeviceName.Trim(), MaxDeviceNameLength);

        return s;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string OrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static List<string> CleanList(List<string>? items, int limit) =>
        (items ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
}
