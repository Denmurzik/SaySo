using SaySo.Core.Audio;

namespace SaySo.Core.Settings;

/// <summary>Несекретные настройки (settings.json).</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    public HotkeySettings Hotkey { get; set; } = new();

    public RecognitionSettings Recognition { get; set; } = new();

    /// <summary>Словарь терминов (до 1000).</summary>
    public List<string> Vocabulary { get; set; } = [];

    /// <summary>Идентификатор устройства (Windows: WASAPI, macOS: UID Core Audio); null — системный микрофон по умолчанию.</summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>
    /// Понятное имя выбранного микрофона (хранится вместе с идентификатором): по нему устройство находится заново,
    /// если идентификатор изменился (другой порт USB). null — как у <see cref="MicrophoneDeviceId"/>.
    /// </summary>
    public string? MicrophoneDeviceName { get; set; }

    public bool SoundsEnabled { get; set; } = true;

    /// <summary>Идентификатор звуковой темы (см. <see cref="SoundThemes"/>); неизвестный заменяется умолчанием.</summary>
    public string SoundTheme { get; set; } = SoundThemes.DefaultId;

    public bool AutostartEnabled { get; set; } = true;

    /// <summary>Значок в трее Windows. Скрытый: настройки открываются повторным запуском SaySo (ярлык в «Пуске»).</summary>
    public bool ShowTrayIcon { get; set; } = true;

    /// <summary>Прозрачность капсулы плашки, 0…100 % (материал Acrylic). Только Windows.</summary>
    public int OverlayTransparency { get; set; } = DefaultOverlayTransparency;

    public const int DefaultOverlayTransparency = 50;

    /// <summary>Запуск демона с правами администратора (для вставки в админ-окна). Только Windows: на macOS не используется.</summary>
    public bool RunAsAdmin { get; set; }

    /// <summary>Раз в сутки (по событиям, не по таймеру) спрашивать GitHub о новой версии.</summary>
    public bool CheckForUpdates { get; set; } = true;
}

public sealed class HotkeySettings
{
    /// <summary>Коды виртуальных клавиш комбинации (VK_*). По умолчанию — правый Ctrl, на macOS правый Cmd.</summary>
    public List<int> Keys { get; set; } = DefaultKeys();

    /// <summary>
    /// Комбинация по умолчанию. На клавиатурах Mac (в том числе MacBook) правого Control нет, а правый Cmd почти не
    /// участвует в сочетаниях и в наборе текста.
    /// </summary>
    public static List<int> DefaultKeys() => OperatingSystem.IsMacOS() ? [0x5C] : [0xA3];

    /// <summary>Нажатие короче порога включает режим «руки свободны».</summary>
    public int TapThresholdMs { get; set; } = 300;
}

public sealed class RecognitionSettings
{
    public const string DefaultGeminiModel = "gemini-3.5-transcribe-live";
    public const string DefaultGeminiEndpoint =
        "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    public const string DefaultGroqBaseUrl = "https://api.groq.com/openai/v1";
    public const string DefaultGroqWhisperModel = "whisper-large-v3";
    public const string DefaultGroqCleanupModel = "openai/gpt-oss-120b";

    public string GeminiModel { get; set; } = DefaultGeminiModel;

    public string GeminiEndpoint { get; set; } = DefaultGeminiEndpoint;

    public TranscriptionMode Mode { get; set; } = TranscriptionMode.Smart;

    /// <summary>Коды BCP-47; пустой список — автоопределение языка.</summary>
    public List<string> LanguageCodes { get; set; } = [];

    public bool GroqFallbackEnabled { get; set; } = true;

    public string GroqBaseUrl { get; set; } = DefaultGroqBaseUrl;

    public string GroqWhisperModel { get; set; } = DefaultGroqWhisperModel;

    public bool GroqCleanupEnabled { get; set; } = true;

    public string GroqCleanupModel { get; set; } = DefaultGroqCleanupModel;
}

public enum TranscriptionMode
{
    Smart,
    Verbatim,
}
