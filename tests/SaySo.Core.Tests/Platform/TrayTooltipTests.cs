using SaySo.Core.Overlay;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class TrayTooltipTests
{
    [Fact]
    public void ShortText_IsUnchanged() => Assert.Equal("SaySo: запись…", TrayTooltip.Fit("SaySo: запись…"));

    [Fact]
    public void ExactlyMaxLength_IsUnchanged()
    {
        var text = new string('а', TrayTooltip.MaxLength);

        Assert.Equal(text, TrayTooltip.Fit(text));
    }

    [Fact]
    public void LongText_IsCutAtWordBoundary_WithEllipsis()
    {
        var text = string.Join(' ', Enumerable.Repeat("слово", 40)); // 239 символов

        var fitted = TrayTooltip.Fit(text);

        Assert.True(fitted.Length <= TrayTooltip.MaxLength);
        Assert.EndsWith("слово…", fitted); // слово не разорвано, пробел перед «…» убран
    }

    [Fact]
    public void LongTextWithoutSpaces_IsCutHard_ToExactLimit()
    {
        var fitted = TrayTooltip.Fit(new string('ш', 300));

        Assert.Equal(TrayTooltip.MaxLength, fitted.Length);
        Assert.EndsWith("ш…", fitted);
    }

    [Fact]
    public void SurrogatePair_IsNotSplit()
    {
        var text = new string('ш', TrayTooltip.MaxLength - 2) + "\U0001F600\U0001F600";

        var fitted = TrayTooltip.Fit(text);

        Assert.True(fitted.Length <= TrayTooltip.MaxLength);
        Assert.DoesNotContain(fitted, char.IsHighSurrogate); // ни одной «висящей» половины
    }

    [Fact]
    public void ForError_WithoutDetail_UsesGenericText() =>
        Assert.Equal("SaySo: последняя диктовка не удалась", TrayTooltip.ForError(null));

    [Fact]
    public void ForError_ShowsFullDetail() =>
        Assert.Equal("SaySo (Gemini: нет связи с сервером; Groq: ключ API недействителен)",
            TrayTooltip.ForError("Gemini: нет связи с сервером; Groq: ключ API недействителен"));

    [Fact]
    public void ForError_LongDetail_IsCutInsideParentheses_AndStaysClosed()
    {
        var tooltip = TrayTooltip.ForError(string.Join(' ', Enumerable.Repeat("слово", 40)));

        Assert.True(tooltip.Length <= TrayTooltip.MaxLength);
        Assert.StartsWith("SaySo (", tooltip);
        Assert.EndsWith("слово…)", tooltip);
    }

    [Fact]
    public void EveryErrorDetail_FitsTooltipWithoutTruncation()
    {
        foreach (var detail in OverlayMessages.AllDetails().Distinct())
        {
            var tooltip = TrayTooltip.ForError(detail);

            Assert.True(tooltip.Length <= TrayTooltip.MaxLength, $"{tooltip.Length}: {tooltip}");
            Assert.DoesNotContain('…', tooltip.Replace("запись…", "")); // полный текст, без обрезки
        }
    }
}
