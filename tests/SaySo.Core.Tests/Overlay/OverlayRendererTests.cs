using SaySo.Core.Overlay;

namespace SaySo.Core.Tests.Overlay;

public class OverlayRendererTests
{
    private static uint[] Render(OverlayFrame f, float scale, out int w, out int h)
    {
        (w, h) = OverlayRenderer.Measure(f, scale);
        var px = new uint[w * h];
        OverlayRenderer.Render(px, w, h, f, scale);
        return px;
    }

    private static OverlayFrame Recording(float level) =>
        new(OverlayMode.Recording, Enumerable.Repeat(level, OverlayRenderer.BarCount).ToArray(), 0);

    private static byte A(uint p) => (byte)(p >> 24);

    private static byte R(uint p) => (byte)(p >> 16);

    private static byte G(uint p) => (byte)(p >> 8);

    [Theory]
    [InlineData(1f, 112, 36)]
    [InlineData(1.5f, 168, 54)]
    [InlineData(2f, 224, 72)]
    public void Measure_Recording(float scale, int w, int h) =>
        Assert.Equal((w, h), OverlayRenderer.Measure(Recording(0), scale));

    [Fact]
    public void Corners_AreTransparent_BodyIsTranslucentDark()
    {
        var px = Render(Recording(0), 1f, out var w, out var h);
        Assert.Equal(0, A(px[0]));
        Assert.Equal(0, A(px[w - 1]));
        Assert.Equal(0, A(px[(h - 1) * w]));
        var body = px[18 * w + 12]; // слева от полосок, по центру по высоте
        Assert.InRange(A(body), 220, 228);   // 0.88 * 255 ≈ 224
        Assert.InRange(R(body), 15, 25);     // тёмный фон (предумноженный)
    }

    [Fact]
    public void Bars_GrowWithLevel()
    {
        static int BrightInColumn(uint[] px, int w, int h, int x)
        {
            var n = 0;
            for (var y = 0; y < h; y++)
            {
                if (R(px[y * w + x]) > 200)
                {
                    n++;
                }
            }

            return n;
        }

        var quiet = Render(Recording(0f), 1f, out var w, out var h);
        var loud = Render(Recording(1f), 1f, out _, out _);
        var x = (112 - (7 * 4 + 6 * 4)) / 2 + 2; // центр первой полоски
        Assert.InRange(BrightInColumn(quiet, w, h, x), 3, 6);
        Assert.InRange(BrightInColumn(loud, w, h, x), 18, 22);
    }

    [Fact]
    public void Message_WidthFollowsText_ErrorDotIsRed()
    {
        var mask = new TextMask(new byte[100 * 16], 100, 16);
        var frame = new OverlayFrame(OverlayMode.Message, [], 0, OverlayTone.Error, mask);
        var px = Render(frame, 1f, out var w, out _);
        Assert.Equal(16 + 8 + 8 + 100 + 16, w);
        var dot = px[18 * w + 20];
        Assert.True(R(dot) > G(dot) + 60);
    }

    [Fact]
    public void Message_WidthIsCapped()
    {
        var mask = new TextMask(new byte[2000 * 16], 2000, 16);
        Assert.Equal(480, OverlayRenderer.Measure(new OverlayFrame(OverlayMode.Message, [], 0, OverlayTone.Info, mask), 1f).Width);
    }

    [Fact]
    public void Processing_DrawsDotsNearCenter()
    {
        var px = Render(new OverlayFrame(OverlayMode.Processing, [], 450), 1f, out var w, out _);
        Assert.True(R(px[18 * w + 56]) > 60);
    }

    [Fact]
    public void Message_EmptyMask_RendersPillAndDotWithoutThrowing()
    {
        var frame = new OverlayFrame(OverlayMode.Message, [], 0, OverlayTone.Error, new TextMask([], 0, 0));
        var px = Render(frame, 1f, out var w, out var h);
        Assert.Equal((112, 36), (w, h));
        Assert.True(A(px[18 * w + 12]) > 200); // фон на месте
        var dot = px[18 * w + 20];
        Assert.True(R(dot) > G(dot) + 60);
    }

    [Fact]
    public void Pixels_ArePremultiplied_ForEveryKind()
    {
        var mask = new TextMask(Enumerable.Range(0, 60 * 16).Select(i => (byte)(i % 256)).ToArray(), 60, 16);
        var frames = new List<OverlayFrame>
        {
            Recording(0f),
            Recording(0.5f),
            Recording(1f),
            new(OverlayMode.Processing, [], 0),
            new(OverlayMode.Processing, [], 450),
        };
        foreach (var tone in Enum.GetValues<OverlayTone>())
        {
            frames.Add(new OverlayFrame(OverlayMode.Message, [], 0, tone, mask));
        }

        foreach (var frame in frames)
        {
            foreach (var scale in new[] { 1f, 1.5f })
            {
                var px = Render(frame, scale, out _, out _);
                foreach (var p in px)
                {
                    var a = A(p);
                    Assert.True(R(p) <= a && G(p) <= a && (byte)p <= a, $"{frame.Mode}/{frame.Tone}@{scale}: не предумножено {p:X8}");
                }
            }
        }
    }

    [Fact]
    public void Render_WrongBufferSize_Throws() =>
        Assert.Throws<ArgumentException>(() => OverlayRenderer.Render(new uint[10], 112, 36, Recording(0), 1f));
}
