namespace SaySo.Core.Engines.Gemini;

/// <param name="QuietMs">Тишина после последнего события (без незавершённого промежуточного текста) → готово.</param>
/// <param name="QuietWithPendingInterimMs">То же, если промежуточный текст ещё не финализирован.</param>
/// <param name="HardTimeoutMs">Жёсткий предел ожидания после конца потока.</param>
/// <param name="SetupTimeoutMs">Ожидание setupComplete.</param>
public sealed record CompletionTimings(
    int QuietMs = 1200,
    int QuietWithPendingInterimMs = 3000,
    int HardTimeoutMs = 8000,
    int SetupTimeoutMs = 10000)
{
    public static CompletionTimings Default { get; } = new();
}

public enum CompletionState
{
    Waiting,
    Done,
    TimedOut,
}

/// <summary>
/// Решает, когда после отправки конца потока весь финальный текст получен. Явный сигнал конца у
/// transcribe-модели не задокументирован, поэтому: конец хода (если пришёл) ИЛИ период тишины ИЛИ таймаут.
/// Не потокобезопасен. Время — миллисекунды монотонных часов.
/// </summary>
public sealed class CompletionPolicy(CompletionTimings timings)
{
    private long? _endSentAt;
    private long _lastEventAt;
    private bool _turnCompleteAfterEnd;

    public bool HasPendingInterim { get; private set; }

    public void OnEndSent(long nowMs)
    {
        _endSentAt = nowMs;
        _lastEventAt = nowMs;
    }

    public void OnEvent(GeminiServerEvent e, long nowMs)
    {
        switch (e.Kind)
        {
            case GeminiEventKind.Interim:
                HasPendingInterim = !string.IsNullOrWhiteSpace(e.Text);
                break;
            case GeminiEventKind.Final:
                HasPendingInterim = false;
                break;
            case GeminiEventKind.TurnComplete when _endSentAt is not null:
                _turnCompleteAfterEnd = true;
                break;
        }

        if (_endSentAt is not null)
        {
            _lastEventAt = nowMs;
        }
    }

    public CompletionState Evaluate(long nowMs)
    {
        if (_endSentAt is not { } end)
        {
            return CompletionState.Waiting;
        }

        if (_turnCompleteAfterEnd && !HasPendingInterim)
        {
            return CompletionState.Done;
        }

        if (nowMs - _lastEventAt >= CurrentQuietMs)
        {
            return CompletionState.Done;
        }

        return nowMs - end >= timings.HardTimeoutMs ? CompletionState.TimedOut : CompletionState.Waiting;
    }

    /// <summary>Когда переоценить состояние, если новых событий не будет.</summary>
    public long NextCheckAt() =>
        _endSentAt is not { } end ? long.MaxValue : Math.Min(_lastEventAt + CurrentQuietMs, end + timings.HardTimeoutMs);

    private int CurrentQuietMs => HasPendingInterim ? timings.QuietWithPendingInterimMs : timings.QuietMs;
}
