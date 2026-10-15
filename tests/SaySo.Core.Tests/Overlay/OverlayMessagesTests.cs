using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Engines;
using SaySo.Core.Input;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Overlay;

public class OverlayMessagesTests
{
    private const int FontPx = 13; // как у OverlayWindow при масштабе 100 %

    [Fact]
    public void EveryOverlayString_FitsInOverlayWidth()
    {
        // Без шрифта плашки GDI подставит другой (Windows 10, серверные раннеры CI): тест измерял бы не тот шрифт.
        Assert.SkipUnless(GdiTextMask.IsTextFontAvailable(),
            $"Шрифт «Segoe UI Variable Text» не установлен (GDI подставил «{GdiTextMask.ResolvedTextFace()}»): ширины строк оверлея не измерить.");
        var tooWide = new List<string>();
        var checkedStrings = 0;
        foreach (var text in OverlayMessages.All().Distinct())
        {
            // Та же раскладка, что рисует плашка: продвижения глифов обычного начертания без хинтинга.
            var width = GlyphText.Measure(text, FontPx);
            Assert.True(width > 0, "GDI не вернул ширину (шрифт или GDI недоступны): " + text);
            TestContext.Current.TestOutputHelper?.WriteLine($"{width,6:F1} px  {text}");
            if (width > OverlayMessages.MaxTextWidthPx)
            {
                tooWide.Add($"{width} px: {text}");
            }

            checkedStrings++;
        }

        Assert.True(checkedStrings > 50, "каталог перечислил подозрительно мало строк");
        Assert.Empty(tooWide);
    }

    [Fact]
    public void OverlayWidthLimit_IsInsideWhatOverlayWindowDraws() =>
        Assert.InRange(OverlayMessages.MaxTextWidthPx, 1, (int)OverlayMotion.MaxTextWidth); // плашка обрезает текст по 400 DIP

    [Fact]
    public void EveryOverlayString_HasNoTrailingPeriod_AndNoEnglishOnlyText()
    {
        foreach (var text in OverlayMessages.All().Distinct())
        {
            Assert.False(text.EndsWith('.'), "точка в конце: " + text);
            Assert.Matches("[А-Яа-я]", text);
        }
    }

    [Fact]
    public void ResolvedFace_ForMissingFont_IsASubstitute_SoAbsenceIsDetectable()
    {
        const string Missing = "Несуществующий шрифт SaySo 12345";

        var face = GdiTextMask.ResolvedTextFace(Missing);

        Assert.False(string.IsNullOrWhiteSpace(face));
        Assert.False(face.StartsWith(Missing, StringComparison.OrdinalIgnoreCase)); // GDI подставил другой шрифт
    }

    [Fact]
    public void ResolvedTextFace_IsReported() => Assert.False(string.IsNullOrWhiteSpace(GdiTextMask.ResolvedTextFace()));

    [Theory]
    [InlineData(InsertOutcome.NoTarget, OverlayMessages.FocusNotRestored)]
    [InlineData(InsertOutcome.ClipboardUnavailable, OverlayMessages.ClipboardBusyRetry)]
    [InlineData(InsertOutcome.BlockedByElevation, "Админ-окно: текст в буфере, вставьте Ctrl+V")]
    [InlineData(InsertOutcome.InputBlocked, "Не вставилось: текст в буфере, нажмите Ctrl+V")]
    [InlineData(InsertOutcome.Inserted, null)]
    public void MenuInsertOutcome_UsesItsOwnWordingWhereHistoryTextIsInvolved(InsertOutcome outcome, string? expected) =>
        Assert.Equal(expected, OverlayMessages.ForMenuInsertOutcome(outcome));

    [Fact]
    public void MenuStrings_AreInTheWidthCatalog()
    {
        var all = OverlayMessages.All().ToList();

        Assert.Contains(OverlayMessages.ClipboardBusyRetry, all);
        Assert.Contains(OverlayMessages.FocusNotRestored, all);
    }

    [Fact]
    public void NoSpeech_IsInTheWidthCatalog_AndNotAnErrorDetail()
    {
        Assert.Equal("Речь не распознана", OverlayMessages.NoSpeech);
        Assert.Contains(OverlayMessages.NoSpeech, OverlayMessages.All());
        Assert.DoesNotContain(OverlayMessages.NoSpeech, OverlayMessages.AllDetails()); // не ошибка: в подсказку значка не попадает
    }

    [Fact]
    public void StartFailed_IsOneVariant() =>
        Assert.Equal("Не удалось начать запись, подробности в журнале", OverlayMessages.StartFailed);

    [Theory]
    [InlineData(InsertOutcome.BlockedByElevation)]
    [InlineData(InsertOutcome.ClipboardUnavailable)]
    [InlineData(InsertOutcome.NoTarget)]
    [InlineData(InsertOutcome.InputBlocked)]
    public void InsertProblems_HaveText(InsertOutcome outcome) =>
        Assert.False(string.IsNullOrWhiteSpace(OverlayMessages.ForInsertOutcome(outcome)));

    [Fact]
    public void InsertSuccess_HasNoText() => Assert.Null(OverlayMessages.ForInsertOutcome(InsertOutcome.Inserted));

    [Fact]
    public void Microphone_AccessDenied_ShortTextPointsToSettings_DetailHasFullPath()
    {
        var text = OverlayMessages.Microphone(MicrophoneErrorKind.AccessDenied, 0);

        Assert.Contains("Параметры", text.Short);
        Assert.Contains("Конфиденциальность и защита → Микрофон", text.Detail);
    }

    [Fact]
    public void Microphone_OtherError_ShowsHexCode() =>
        Assert.Equal("Ошибка микрофона (0x80004005)", OverlayMessages.Microphone(MicrophoneErrorKind.Other, unchecked((int)0x80004005)).Short);

    [Theory]
    [InlineData(EngineErrorKind.QuotaExceeded)]
    [InlineData(EngineErrorKind.AuthFailed)]
    [InlineData(EngineErrorKind.RegionBlocked)]
    [InlineData(EngineErrorKind.Network)]
    public void NoFallback_MentionsThatReserveIsNotConfigured(EngineErrorKind kind)
    {
        var text = OverlayMessages.NoFallback(kind);

        Assert.StartsWith("Gemini", text.Short);
        Assert.EndsWith("резерв не настроен", text.Short);
        Assert.EndsWith("резерв не настроен", text.Detail);
    }

    [Fact]
    public void NoFallback_GeminiNotConfigured_JustSaysNoKey() =>
        Assert.Equal("Gemini: не задан ключ API", OverlayMessages.NoFallback(EngineErrorKind.NotConfigured).Short);

    [Fact]
    public void BothFailed_ShortTextPointsToTray_DetailHasBothReasons()
    {
        var text = OverlayMessages.BothFailed(EngineErrorKind.RegionBlocked, EngineErrorKind.Network);

        Assert.Equal("Gemini и Groq недоступны, подробности в трее", text.Short);
        Assert.Equal("Gemini: недоступен в вашем регионе, проверьте VPN; Groq: нет связи с сервером", text.Detail);
    }

    [Fact]
    public void BothFailed_GroqOnlyMode_DoesNotMentionGemini()
    {
        var text = OverlayMessages.BothFailed(EngineErrorKind.NotConfigured, EngineErrorKind.AuthFailed);

        Assert.Equal("Groq: ключ недействителен", text.Short);
        Assert.Equal("Groq: ключ API недействителен", text.Detail);
    }

    [Fact]
    public void FallbackUsed_NamesTheReason() =>
        Assert.Equal("Резерв Groq (Gemini: лимит исчерпан)", OverlayMessages.FallbackUsed(EngineErrorKind.QuotaExceeded));
}
