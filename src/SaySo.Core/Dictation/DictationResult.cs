using SaySo.Core.Engines;
using SaySo.Core.Overlay;

namespace SaySo.Core.Dictation;

/// <param name="Engine">"gemini", "groq" или "none".</param>
/// <param name="PrimaryError">Почему не сработал основной движок (если текст дал резерв).</param>
public sealed record DictationResult(string Text, string Engine, EngineErrorKind? PrimaryError)
{
    public static DictationResult Empty { get; } = new("", "none", null);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>
/// Диктовка не удалась ни одним движком. <see cref="UserMessage"/> — короткий текст для плашки оверлея,
/// <see cref="Detail"/> — полный (подсказка значка в трее).
/// </summary>
public sealed class DictationFailedException(EngineErrorKind? primaryError, EngineErrorKind? fallbackError, FailureText text)
    : Exception(text.Short)
{
    public EngineErrorKind? PrimaryError { get; } = primaryError;

    public EngineErrorKind? FallbackError { get; } = fallbackError;

    public string UserMessage { get; } = text.Short;

    public string Detail { get; } = text.Detail;
}
