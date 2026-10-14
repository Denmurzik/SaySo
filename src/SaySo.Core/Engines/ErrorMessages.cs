namespace SaySo.Core.Engines;

/// <summary>Короткие русские сообщения об ошибках для оверлея и окна настроек.</summary>
public static class ErrorMessages
{
    public static string Describe(EngineErrorKind kind, string engine) => kind switch
    {
        EngineErrorKind.QuotaExceeded => $"{engine}: лимит запросов исчерпан",
        EngineErrorKind.AuthFailed => $"{engine}: ключ API недействителен",
        EngineErrorKind.RegionBlocked => $"{engine}: недоступен в вашем регионе — проверьте VPN",
        EngineErrorKind.ModelNotFound => $"{engine}: модель не найдена",
        EngineErrorKind.Network => $"{engine}: нет связи с сервером",
        EngineErrorKind.Timeout => $"{engine}: сервер не ответил вовремя",
        EngineErrorKind.Protocol => $"{engine}: ошибка протокола",
        EngineErrorKind.NotConfigured => $"{engine}: не задан ключ API",
        _ => $"{engine}: неизвестная ошибка",
    };
}
