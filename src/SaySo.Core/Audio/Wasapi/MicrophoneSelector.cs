namespace SaySo.Core.Audio.Wasapi;

/// <summary>Конечная точка записи (идентификатор, понятное имя, подключена ли) — вход для <see cref="MicrophoneSelector"/>.</summary>
public sealed record CaptureEndpoint(string Id, string Name, bool IsActive);

public enum MicrophoneMatch
{
    /// <summary>Конкретный микрофон не выбран — системный по умолчанию.</summary>
    NotRequested,

    /// <summary>Найдено устройство с сохранённым идентификатором.</summary>
    ById,

    /// <summary>Идентификатора нет среди активных, но подключено устройство с тем же именем (другой порт USB).</summary>
    ByName,

    /// <summary>Сохранённого микрофона нет — придётся использовать микрофон Windows по умолчанию.</summary>
    Fallback,
}

/// <param name="Match">Как выбор сопоставился с подключёнными устройствами.</param>
/// <param name="Endpoint">Найденная активная точка; null при <see cref="MicrophoneMatch.NotRequested"/> и <see cref="MicrophoneMatch.Fallback"/>.</param>
public sealed record MicrophoneResolution(MicrophoneMatch Match, CaptureEndpoint? Endpoint);

/// <summary>Сопоставляет сохранённый выбор микрофона (идентификатор + имя) с подключёнными устройствами.</summary>
public static class MicrophoneSelector
{
    public static MicrophoneResolution Resolve(string? savedId, string? savedName, IEnumerable<CaptureEndpoint> endpoints)
    {
        var hasId = !string.IsNullOrWhiteSpace(savedId);
        var wantedName = NormalizeName(savedName);
        if (!hasId && wantedName.Length == 0)
        {
            return new MicrophoneResolution(MicrophoneMatch.NotRequested, null);
        }

        var active = endpoints.Where(e => e.IsActive).ToList();
        if (hasId && active.FirstOrDefault(e => string.Equals(e.Id, savedId, StringComparison.Ordinal)) is { } byId)
        {
            return new MicrophoneResolution(MicrophoneMatch.ById, byId);
        }

        if (wantedName.Length > 0
            && active.FirstOrDefault(e => string.Equals(NormalizeName(e.Name), wantedName, StringComparison.OrdinalIgnoreCase)) is { } byName)
        {
            return new MicrophoneResolution(MicrophoneMatch.ByName, byName);
        }

        return new MicrophoneResolution(MicrophoneMatch.Fallback, null);
    }

    /// <summary>
    /// Имя для сравнения: без пробелов по краям и без номера порта, который Windows добавляет внутри скобок
    /// при подключении того же USB-устройства в другой порт («Микрофон (2- USB Audio)» → «Микрофон (USB Audio)»).
    /// </summary>
    public static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "";
        }

        var s = name.Trim();
        var open = s.IndexOf('(');
        if (open < 0)
        {
            return s;
        }

        var i = open + 1;
        var digitsStart = i;
        while (i < s.Length && char.IsAsciiDigit(s[i]))
        {
            i++;
        }

        if (i > digitsStart && i + 1 < s.Length && s[i] == '-' && s[i + 1] == ' ')
        {
            return string.Concat(s.AsSpan(0, open + 1), s.AsSpan(i + 2));
        }

        return s;
    }
}
