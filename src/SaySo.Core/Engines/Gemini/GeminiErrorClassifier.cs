namespace SaySo.Core.Engines.Gemini;

/// <summary>Классификация ошибок Gemini Live: сначала по тексту причины закрытия, затем по коду.</summary>
public static class GeminiErrorClassifier
{
    public static EngineErrorKind Classify(int? closeStatus, string? reason)
    {
        var r = reason ?? "";
        if (Has(r, "quota") || Has(r, "resource_exhausted") || Has(r, "rate limit"))
        {
            return EngineErrorKind.QuotaExceeded;
        }

        if (Has(r, "location") || Has(r, "region"))
        {
            return EngineErrorKind.RegionBlocked;
        }

        if (Has(r, "api key") || Has(r, "unauthenticated") || Has(r, "permission_denied")
            || Has(r, "denied access") || Has(r, "unregistered callers"))
        {
            return EngineErrorKind.AuthFailed;
        }

        if (Has(r, "not found") || Has(r, "not_found"))
        {
            return EngineErrorKind.ModelNotFound;
        }

        if (Has(r, "unavailable") || Has(r, "internal error") || Has(r, "deadline"))
        {
            return EngineErrorKind.Network;
        }

        return closeStatus switch
        {
            null or 1001 or 1006 => EngineErrorKind.Network,
            1011 when r.Length == 0 => EngineErrorKind.Network,
            _ => EngineErrorKind.Protocol,
        };
    }

    public static EngineErrorKind ClassifyHandshake(int httpStatus) => httpStatus switch
    {
        401 or 403 => EngineErrorKind.AuthFailed,
        429 => EngineErrorKind.QuotaExceeded,
        404 => EngineErrorKind.ModelNotFound,
        0 or >= 500 => EngineErrorKind.Network,
        _ => EngineErrorKind.Protocol,
    };

    private static bool Has(string text, string fragment) =>
        text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
