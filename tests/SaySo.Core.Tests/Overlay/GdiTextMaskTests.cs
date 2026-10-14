using SaySo.Core.Overlay;

namespace SaySo.Core.Tests.Overlay;

public class GdiTextMaskTests
{
    [Fact]
    public void RenderText_ProducesVisibleCoverage()
    {
        var mask = GdiTextMask.RenderText("Ошибка сети", 14, 400);
        Assert.InRange(mask.Width, 30, 400);
        Assert.InRange(mask.Height, 14, 30);
        Assert.Equal(mask.Width * mask.Height, mask.Alpha.Length);
        Assert.Contains(mask.Alpha, a => a > 200);
    }

    [Fact]
    public void RenderText_RespectsMaxWidth() =>
        Assert.True(GdiTextMask.RenderText(new string('Ш', 200), 14, 120).Width <= 120);

    [Fact]
    public void RenderGlyph_IsSquareAndNotEmpty()
    {
        var mask = GdiTextMask.RenderGlyph('', "Segoe Fluent Icons", 32);
        Assert.Equal(32, mask.Width);
        Assert.Equal(32, mask.Height);
        Assert.Contains(mask.Alpha, a => a > 200);
    }
}
