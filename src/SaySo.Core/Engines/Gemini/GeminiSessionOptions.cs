using SaySo.Core.Settings;

namespace SaySo.Core.Engines.Gemini;

public sealed record GeminiSessionOptions(
    string Model,
    TranscriptionMode Mode,
    IReadOnlyList<string> LanguageCodes,
    IReadOnlyList<string> Vocabulary);
