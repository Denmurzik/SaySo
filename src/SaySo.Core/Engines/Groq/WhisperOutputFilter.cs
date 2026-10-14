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

    /// <summary>Максимальная длина остатка после фразы-галлюцинации (подпись вроде «DimaTorzok»).</summary>
    private const int MaxCreditsLength = 40;

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

            // Галлюцинация — короткая фраза целиком (плюс, возможно, подпись), а не начало реальной речи:
            // дальше нет запятой, остаток короткий и после него не начинается новое предложение.
            var rest = core[prefix.Length..];
            if (rest.Contains(','))
            {
                continue;
            }

            if (rest.Trim('.', '!', '?', '…', ' ').Length <= MaxCreditsLength && !HasSentenceBoundary(rest))
            {
                return "";
            }
        }

        return trimmed;
    }

    /// <summary>Есть ли конец предложения, за которым идёт ещё текст («. Теперь перейдём…»).</summary>
    private static bool HasSentenceBoundary(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…'))
            {
                continue;
            }

            var j = i + 1;
            while (j < text.Length && char.IsWhiteSpace(text[j]))
            {
                j++;
            }

            if (j > i + 1 && j < text.Length)
            {
                return true;
            }
        }

        return false;
    }
}
