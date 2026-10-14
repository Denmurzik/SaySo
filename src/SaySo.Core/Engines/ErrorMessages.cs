namespace SaySo.Core.Engines;

/// <summary>Короткие русские сообщения об ошибках для оверлея, подсказки трея и окна настроек.</summary>
public static class ErrorMessages
{
    public static string Describe(EngineErrorKind kind, string engine) => kind switch
    {
        EngineErrorKind.QuotaExceeded => $"{engine}: лимит запросов исчерпан",
        EngineErrorKind.AuthFailed => $"{engine}: ключ API недействителен",
        EngineErrorKind.RegionBlocked => $"{engine}: недоступен в вашем регионе, проверьте VPN",
        EngineErrorKind.ModelNotFound => $"{engine}: модель не найдена",
        EngineErrorKind.Network => $"{engine}: нет связи с сервером",
        EngineErrorKind.Timeout => $"{engine}: сервер не ответил вовремя",
        EngineErrorKind.Protocol => $"{engine}: ошибка протокола",
        EngineErrorKind.NotConfigured => $"{engine}: не задан ключ API",
        EngineErrorKind.InvalidEndpoint => $"{engine}: неверный адрес сервера",
        _ => $"{engine}: неизвестная ошибка",
    };

    /// <summary>
    /// То же короче: вместе с приставкой «Резерв Groq (…)» или припиской «; резерв не настроен» текст должен поместиться на
    /// плашку оверлея (<see cref="Overlay.OverlayMessages.MaxTextWidthPx"/>). Полный вариант — <see cref="Describe"/>.
    /// </summary>
    public static string DescribeShort(EngineErrorKind kind, string engine) => kind switch
    {
        EngineErrorKind.QuotaExceeded => $"{engine}: лимит исчерпан",
        EngineErrorKind.AuthFailed => $"{engine}: ключ недействителен",
        EngineErrorKind.RegionBlocked => $"{engine}: недоступен без VPN",
        EngineErrorKind.Timeout => $"{engine}: нет ответа",
        EngineErrorKind.InvalidEndpoint => $"{engine}: неверный адрес",
        _ => Describe(kind, engine),
    };
}
