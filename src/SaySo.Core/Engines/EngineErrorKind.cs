namespace SaySo.Core.Engines;

public enum EngineErrorKind
{
    QuotaExceeded,
    AuthFailed,
    RegionBlocked,
    ModelNotFound,
    Network,
    Timeout,
    Protocol,
    NotConfigured,

    /// <summary>Адрес сервера в настройках не является корректным URL.</summary>
    InvalidEndpoint,
}

/// <summary>Ошибка движка распознавания. Сообщение не содержит ключей и текста диктовки.</summary>
public sealed class TranscriptionException(EngineErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public EngineErrorKind Kind { get; } = kind;
}
