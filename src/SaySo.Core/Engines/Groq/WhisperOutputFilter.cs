namespace SaySo.Core.Engines.Groq;

/// <summary>Отсекает типичные галлюцинации Whisper на тишине (только если это весь текст целиком).</summary>
public static class WhisperOutputFilter
{
    private static readonly string[] HallucinationPrefixes =
    [
        "продолжение следует",
        "субтитры сделал",
        "субтитры создавал",
        "субтитры подогнал",
        "редактор субтитров",
        "спасибо за просмотр",
        "thank you for watching",
        "thanks for watching",
    ];

    public static string Clean(string text)
    {
        var trimmed = text.Trim();
        var core = trimmed.Trim('.', '!', '?', '…', ' ', ',');
        if (core.Length == 0)
        {
            return "";
        }

        foreach (var prefix in HallucinationPrefixes)
        {
            if (!core.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Галлюцинация — это короткая фраза целиком (плюс, возможно, подпись), а не начало реальной речи.
            var rest = core[prefix.Length..];
            if (rest.Length == 0 || !rest.Contains(','))
            {
                return "";
            }
        }

        return trimmed;
    }
}
