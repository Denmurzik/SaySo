using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Engines;
using SaySo.Core.Input;
using SaySo.Core.Updates;

namespace SaySo.Core.Overlay;

/// <summary>
/// Короткий текст для плашки оверлея и полный текст для подсказки трея и страниц настроек.
/// <see cref="Short"/> обязан помещаться в <see cref="OverlayMessages.MaxTextWidthPx"/>; <see cref="Detail"/> может быть длиннее.
/// </summary>
public readonly record struct FailureText(string Short, string Detail)
{
    public static FailureText Same(string text) => new(text, text);
}

/// <summary>
/// Каталог всех строк, которые попадают на плашку оверлея. Плашка однострочная и шириной не больше 400 DIP,
/// поэтому каждая строка короткая (≤ <see cref="MaxTextWidthPx"/> при Segoe UI Variable Text 13 px), без точки в конце;
/// полный текст ошибки лежит в <see cref="FailureText.Detail"/> и показывается в подсказке значка в трее.
/// <see cref="All"/> перечисляет все варианты — по нему модульный тест замеряет ширину.
/// </summary>
public static class OverlayMessages
{
    /// <summary>Допустимая ширина текста: плашка рисует до 400 px, остальное — запас на другие шрифты и DPI.</summary>
    public const int MaxTextWidthPx = 380;

    public const string StartFailed = "Не удалось начать запись — подробности в журнале";
    public const string DictationError = "Ошибка диктовки — подробности в журнале";

    /// <summary>«Скопировать последнее» / «Вставить последнее» при занятом буфере: текст и так в истории, поможет повтор.</summary>
    public const string ClipboardBusyRetry = "Буфер обмена занят — попробуйте ещё раз";

    /// <summary>«Вставить последнее»: окно, куда вставляли, не получило фокус (или закрыто).</summary>
    public const string FocusNotRestored = "Не удалось вернуться в окно — текст в истории";

    // Обновление: плашка показывается, пока идёт скачивание и после отмены/сбоя запуска.
    public const string UpdateDownloading = "Скачиваю обновление…";
    public const string UpdateCancelled = "Обновление отменено";
    public const string UpdateLaunchFailed = "Не удалось запустить обновление";
    public const string UpdateUpToDate = "Установлена последняя версия";
    public const string UpdateCheckFailed = "Не удалось проверить обновления";

    private const string NoFallbackSuffix = "; резерв не настроен";
    private const string BothUnavailable = "Gemini и Groq недоступны — подробности в трее";

    public static FailureText Microphone(MicrophoneErrorKind kind, int nativeError) => kind switch
    {
        MicrophoneErrorKind.AccessDenied => new FailureText(
            "Нет доступа к микрофону — см. Параметры",
            "Нет доступа к микрофону: Параметры → Конфиденциальность и защита → Микрофон → разрешите доступ классическим приложениям"),
        MicrophoneErrorKind.NotFound => FailureText.Same("Микрофон не найден"),
        MicrophoneErrorKind.DeviceLost => FailureText.Same("Микрофон отключился во время записи"),
        MicrophoneErrorKind.InUse => FailureText.Same("Микрофон занят другим приложением"),
        _ => FailureText.Same($"Ошибка микрофона (0x{nativeError:X8})"),
    };

    /// <summary>null — вставка удалась, сообщать нечего.</summary>
    public static string? ForInsertOutcome(InsertOutcome outcome) => outcome switch
    {
        InsertOutcome.BlockedByElevation => "Админ-окно: текст в буфере, вставьте Ctrl+V",
        InsertOutcome.ClipboardUnavailable => "Буфер обмена занят — текст в истории",
        InsertOutcome.NoTarget => "Нет активного окна — текст в истории",
        InsertOutcome.InputBlocked => "Не вставилось — текст в буфере, нажмите Ctrl+V",
        _ => null,
    };

    /// <summary>
    /// То же для пункта меню трея «Вставить последнее»: текст берётся из истории, поэтому «окна нет» означает, что фокус
    /// не вернулся, а занятый буфер лечится повтором. null — вставка удалась.
    /// </summary>
    public static string? ForMenuInsertOutcome(InsertOutcome outcome) => outcome switch
    {
        InsertOutcome.NoTarget => FocusNotRestored,
        InsertOutcome.ClipboardUnavailable => ClipboardBusyRetry,
        _ => ForInsertOutcome(outcome),
    };

    /// <summary>Текст вставил резерв: сообщаем, почему не основной движок.</summary>
    public static string FallbackUsed(EngineErrorKind primaryError) =>
        "Резерв Groq — " + ErrorMessages.DescribeShort(primaryError, "Gemini");

    /// <summary>Основной движок не сработал, а резерв выключен или без ключа.</summary>
    public static FailureText NoFallback(EngineErrorKind primaryError) =>
        primaryError == EngineErrorKind.NotConfigured
            ? FailureText.Same(ErrorMessages.Describe(primaryError, "Gemini"))
            : new FailureText(
                ErrorMessages.DescribeShort(primaryError, "Gemini") + NoFallbackSuffix,
                ErrorMessages.Describe(primaryError, "Gemini") + NoFallbackSuffix);

    /// <summary>Не сработал ни основной движок, ни резерв.</summary>
    public static FailureText BothFailed(EngineErrorKind primaryError, EngineErrorKind fallbackError) =>
        primaryError == EngineErrorKind.NotConfigured
            ? new FailureText(ErrorMessages.DescribeShort(fallbackError, "Groq"), ErrorMessages.Describe(fallbackError, "Groq")) // режим «только Groq»: Gemini не упоминаем
            : new FailureText(BothUnavailable,
                ErrorMessages.Describe(primaryError, "Gemini") + "; " + ErrorMessages.Describe(fallbackError, "Groq"));

    /// <summary>Скачивание или проверка файла обновления не удались; в журнале — подробности.</summary>
    public static string ForDownloadFailure(DownloadFailure failure) => failure switch
    {
        DownloadFailure.NotInstallable => "В релизе нет установщика",
        DownloadFailure.ChecksumFileInvalid or DownloadFailure.SizeMismatch or DownloadFailure.HashMismatch =>
            "Файл обновления не прошёл проверку",
        _ => "Не удалось скачать обновление",
    };

    /// <summary>Все возможные строки плашки (по одной на вариант) — для проверки ширины.</summary>
    public static IEnumerable<string> All() => AllTexts().Select(t => t.Short).Concat(UpdateTexts());

    /// <summary>Полные тексты для подсказки трея — для проверки, что они помещаются в её лимит.</summary>
    public static IEnumerable<string> AllDetails() => AllTexts().Select(t => t.Detail);

    // Сообщения об обновлении — не ошибки диктовки и в подсказку значка не попадают, поэтому не входят в AllDetails().
    private static IEnumerable<string> UpdateTexts()
    {
        yield return UpdateDownloading;
        yield return UpdateCancelled;
        yield return UpdateLaunchFailed;
        yield return UpdateUpToDate;
        yield return UpdateCheckFailed;
        foreach (var failure in Enum.GetValues<DownloadFailure>())
        {
            yield return ForDownloadFailure(failure);
        }
    }

    private static IEnumerable<FailureText> AllTexts()
    {
        yield return FailureText.Same(StartFailed);
        yield return FailureText.Same(DictationError);

        foreach (var kind in Enum.GetValues<MicrophoneErrorKind>())
        {
            // Для «прочих» ошибок в тексте виден код: перебираем каждую шестнадцатеричную цифру (ширина букв A–F разная).
            for (var nibble = 0; nibble < 16; nibble++)
            {
                yield return Microphone(kind, unchecked((int)(0x11111111u * (uint)nibble)));
            }
        }

        foreach (var outcome in Enum.GetValues<InsertOutcome>())
        {
            if (ForInsertOutcome(outcome) is { } text)
            {
                yield return FailureText.Same(text);
            }

            if (ForMenuInsertOutcome(outcome) is { } menuText)
            {
                yield return FailureText.Same(menuText);
            }
        }

        var kinds = Enum.GetValues<EngineErrorKind>();
        foreach (var primary in kinds)
        {
            yield return FailureText.Same(FallbackUsed(primary));
            yield return NoFallback(primary);
            foreach (var fallback in kinds)
            {
                yield return BothFailed(primary, fallback);
            }
        }
    }
}
