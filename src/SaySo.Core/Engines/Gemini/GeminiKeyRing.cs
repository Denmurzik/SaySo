namespace SaySo.Core.Engines.Gemini;

/// <summary>
/// Несколько ключей Gemini с автопереключением: используется текущий ключ; исчерпавший квоту или
/// недействительный ключ ставится на паузу, и следующий запрос берёт следующий доступный ключ.
/// Состояние живёт в памяти процесса. Потокобезопасно.
/// </summary>
public sealed class GeminiKeyRing(TimeProvider time)
{
    public static readonly TimeSpan QuotaPause = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan InvalidKeyPause = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, EngineErrorKind Reason)> _paused = new(StringComparer.Ordinal);
    private List<string> _keys = [];
    private int _current;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _keys.Count;
            }
        }
    }

    public void SetKeys(IReadOnlyList<string> keys)
    {
        lock (_gate)
        {
            var currentKey = _keys.Count > 0 ? _keys[_current] : null;
            _keys = keys.Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => k.Trim()).Distinct(StringComparer.Ordinal).ToList();
            foreach (var stale in _paused.Keys.Where(k => !_keys.Contains(k)).ToList())
            {
                _paused.Remove(stale);
            }

            _current = currentKey is null ? 0 : Math.Max(0, _keys.IndexOf(currentKey));
        }
    }

    public string? Acquire() => Acquire(out _);

    /// <param name="pausedReason">
    /// Если свободного ключа нет, а ключи есть (все на паузе), — причина паузы того ключа, который освободится первым
    /// (<see cref="EngineErrorKind.QuotaExceeded"/> или <see cref="EngineErrorKind.AuthFailed"/>); иначе
    /// <see cref="EngineErrorKind.NotConfigured"/>.
    /// </param>
    public string? Acquire(out EngineErrorKind pausedReason)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            (DateTimeOffset Until, EngineErrorKind Reason)? soonest = null;
            for (var i = 0; i < _keys.Count; i++)
            {
                var index = (_current + i) % _keys.Count;
                var key = _keys[index];
                if (_paused.TryGetValue(key, out var pause) && pause.Until > now)
                {
                    if (soonest is null || pause.Until < soonest.Value.Until)
                    {
                        soonest = pause;
                    }

                    continue;
                }

                _paused.Remove(key);
                _current = index;
                pausedReason = EngineErrorKind.NotConfigured;
                return key;
            }

            pausedReason = soonest?.Reason ?? EngineErrorKind.NotConfigured;
            return null;
        }
    }

    /// <summary>
    /// Снимает все паузы. Вызывается, когда пользователь изменил настройки (ключ мог быть исправлен, VPN включён):
    /// иначе недействительный по ошибке ключ молчал бы целый час.
    /// </summary>
    public void ResetPauses()
    {
        lock (_gate)
        {
            _paused.Clear();
        }
    }

    public void ReportFailure(string key, EngineErrorKind kind)
    {
        TimeSpan pause;
        switch (kind)
        {
            case EngineErrorKind.QuotaExceeded:
                pause = QuotaPause;
                break;
            case EngineErrorKind.AuthFailed:
                pause = InvalidKeyPause;
                break;
            default:
                return; // сеть, регион, протокол — ключ не виноват
        }

        lock (_gate)
        {
            if (_keys.Contains(key))
            {
                _paused[key] = (time.GetUtcNow() + pause, kind);
            }
        }
    }

    /// <summary>Номер ключа с 1 (для журнала — сам ключ не логируется); 0 — такого ключа нет.</summary>
    public int NumberOf(string key)
    {
        lock (_gate)
        {
            return _keys.IndexOf(key) + 1;
        }
    }
}
