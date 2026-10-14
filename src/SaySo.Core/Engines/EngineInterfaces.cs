namespace SaySo.Core.Engines;

/// <summary>Потоковая сессия распознавания одной реплики.</summary>
public interface IStreamingSession : IAsyncDisposable
{
    /// <summary>Промежуточный текст (может вызываться из фонового потока).</summary>
    event Action<string>? InterimText;

    /// <summary>
    /// Сессия завершилась ошибкой: один раз, из фонового потока, до того как ошибку получит CompleteAsync.
    /// По нему кольцо ключей ставит ключ на паузу, а реплика сразу переключается на следующий ключ.
    /// </summary>
    event Action<TranscriptionException>? Failed;

    /// <summary>Неблокирующая постановка фрагмента PCM в очередь отправки. Потокобезопасно.</summary>
    void PushAudio(ReadOnlyMemory<byte> chunk);

    /// <summary>Конец аудио: дождаться итогового текста. Бросает <see cref="TranscriptionException"/>.</summary>
    Task<string> CompleteAsync(CancellationToken ct);
}

public interface IStreamingEngine
{
    /// <summary>
    /// Создаёт и запускает сессию. Без ключа — TranscriptionException(NotConfigured); все ключи на паузе —
    /// TranscriptionException с причиной паузы (QuotaExceeded или AuthFailed), без обращения к сети.
    /// </summary>
    IStreamingSession StartSession();
}

/// <summary>Распознавание готовой записи (WAV) — резервный путь.</summary>
public interface IBatchEngine
{
    string Name { get; }

    Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);
}
