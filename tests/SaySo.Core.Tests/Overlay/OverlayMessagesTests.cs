using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Engines;
using SaySo.Core.Input;
using SaySo.Core.Overlay;

namespace SaySo.Core.Tests.Overlay;

public class OverlayMessagesTests
{
    private const int FontPx = 13; // как у OverlayWindow при масштабе 100 %

    [Fact]
    public void EveryOverlayString_FitsInOverlayWidth()
    {
        var tooWide = new List<string>();
        var checkedStrings = 0;
        foreach (var text in OverlayMessages.All().Distinct())
        {
            // Огромный лимит: многоточие не ставится, ширина маски — настоящая ширина текста (+2 px запаса RenderText).
            var mask = GdiTextMask.RenderText(text, FontPx, 10_000);
            Assert.True(mask.Width > 0, "GDI не вернул маску (шрифт или GDI недоступны): " + text);
            var width = mask.Width - 2;
            TestContext.Current.TestOutputHelper?.WriteLine($"{width,4} px  {text}");
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
        Assert.InRange(OverlayMessages.MaxTextWidthPx, 1, 400); // OverlayWindow.ShowMessage: RenderText(…, 400 · scale)

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
    public void StartFailed_IsOneVariant() =>
        Assert.Equal("Не удалось начать запись — подробности в журнале", OverlayMessages.StartFailed);

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

        Assert.Equal("Gemini и Groq недоступны — подробности в трее", text.Short);
        Assert.Equal("Gemini: недоступен в вашем регионе — проверьте VPN; Groq: нет связи с сервером", text.Detail);
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
        Assert.Equal("Резерв Groq — Gemini: лимит исчерпан", OverlayMessages.FallbackUsed(EngineErrorKind.QuotaExceeded));
}
