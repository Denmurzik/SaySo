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
    private readonly Dictionary<string, DateTimeOffset> _pausedUntil = new(StringComparer.Ordinal);
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
            foreach (var stale in _pausedUntil.Keys.Where(k => !_keys.Contains(k)).ToList())
            {
                _pausedUntil.Remove(stale);
            }

            _current = currentKey is null ? 0 : Math.Max(0, _keys.IndexOf(currentKey));
        }
    }

    public string? Acquire()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            for (var i = 0; i < _keys.Count; i++)
            {
                var index = (_current + i) % _keys.Count;
                var key = _keys[index];
                if (_pausedUntil.TryGetValue(key, out var until) && until > now)
                {
                    continue;
                }

                _pausedUntil.Remove(key);
                _current = index;
                return key;
            }

            return null;
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
                _pausedUntil[key] = time.GetUtcNow() + pause;
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
