namespace SaySo.Core.Platform;

/// <summary>Тексты подсказки значка в трее. В <c>NOTIFYICONDATAW.szTip</c> помещается 127 символов и завершающий ноль.</summary>
public static class TrayTooltip
{
    public const int MaxLength = 127;

    private const string Prefix = "SaySo: ";
    private const string ErrorOpen = "SaySo (";
    private const string ErrorClose = ")";

    public const string Paused = Prefix + "приостановлено";

    public const string Recording = Prefix + "запись…";

    /// <summary>Подсказка в покое: как начать диктовку (<paramref name="hotkey"/> — из <c>HotkeyFormatter</c>).</summary>
    public static string Idle(string hotkey) => Prefix + "удерживайте " + hotkey;

    /// <summary>
    /// Подсказка для состояния «ошибка»: полный текст последней ошибки в скобках (<paramref name="detail"/> — из
    /// <c>FailureText.Detail</c>; у него свои двоеточия, поэтому после «SaySo» двоеточия нет). Слишком длинный текст
    /// сокращается внутри скобок, скобка остаётся закрытой.
    /// </summary>
    public static string ForError(string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? Prefix + "последняя диктовка не удалась"
            : ErrorOpen + Fit(detail.Trim(), MaxLength - ErrorOpen.Length - ErrorClose.Length) + ErrorClose;

    /// <summary>
    /// Укладывает текст в <paramref name="max"/> символов: лишнее отрезается по границе слова и заменяется на «…»
    /// (суррогатная пара не разрывается).
    /// </summary>
    public static string Fit(string text, int max = MaxLength)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = max - 1; // место под «…»
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        var space = text.LastIndexOf(' ', cut - 1, Math.Min(cut, 24));
        if (space > 0)
        {
            cut = space;
        }

        return text[..cut].TrimEnd(' ', ',', ';', ':', '—', '-') + "…";
    }
}
