using SaySo.Core.Overlay;

namespace SaySo.Core.Tests.Overlay;

public class OverlaySpringTests
{
    [Fact]
    public void Reveal_StartsAtFrom_AndSettlesAtOne()
    {
        Assert.Equal(0, OverlaySpring.Reveal(0, 0));
        Assert.Equal(0.4, OverlaySpring.Reveal(0.4, 0), 6);
        Assert.Equal(1, OverlaySpring.Reveal(0, OverlaySpring.SettleSeconds));
        Assert.InRange(OverlaySpring.Reveal(0, 0.55), 0.99, 1.01); // посадка к 0,45–0,55 с
    }

    [Fact]
    public void Reveal_OvershootsAboutTwoPercent()
    {
        var peak = Enumerable.Range(1, 800).Select(ms => OverlaySpring.Reveal(0, ms / 1000.0)).Max();

        Assert.InRange(peak, 1.01, 1.03);
    }

    [Fact]
    public void Reveal_ReachesTheFinishAroundThreeHundredMilliseconds()
    {
        var firstAtOne = Enumerable.Range(1, 800).First(ms => OverlaySpring.Reveal(0, ms / 1000.0) >= 1);

        Assert.InRange(firstAtOne, 180, 320);
    }

    [Fact]
    public void Hide_GoesFromCurrentToZero_EaseIn()
    {
        var total = OverlayMotion.HideDuration.TotalSeconds;

        Assert.Equal(1, OverlaySpring.Hide(1, 0), 6);
        Assert.Equal(0, OverlaySpring.Hide(1, total));
        Assert.True(OverlaySpring.Hide(1, total / 2) > 0.5, "ease-in: в первой половине пройдено меньше половины пути");
        Assert.Equal(0.5 * OverlaySpring.Hide(1, total / 3), OverlaySpring.Hide(0.5, total / 3), 6);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    public void CubicBezier_Endpoints(double x, double y) =>
        Assert.Equal(y, OverlaySpring.CubicBezier(OverlayMotion.HideEasing, x), 5);

    [Fact]
    public void CubicBezier_IsMonotonic()
    {
        var values = Enumerable.Range(0, 101).Select(i => OverlaySpring.CubicBezier(OverlayMotion.HideEasing, i / 100.0)).ToList();

        Assert.True(values.Zip(values.Skip(1)).All(p => p.Second >= p.First - 1e-9));
    }

    [Fact]
    public void EaseOut_Bounds()
    {
        Assert.Equal(0, OverlaySpring.EaseOut(-1));
        Assert.Equal(1, OverlaySpring.EaseOut(2));
        Assert.True(OverlaySpring.EaseOut(0.5) > 0.5);
    }
}
