using SaySo.Core.Interop;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using Windows.Win32.Graphics.Direct2D;

namespace SaySo.Core.Tests.Platform;

/// <summary>Текст плашки контурами глифов: только GDI и фабрика D2D, без окон и устройства D3D.</summary>
public unsafe class GlyphTextTests
{
    private const int FontPx = 13;

    [Fact]
    public void Measure_GrowsWithText()
    {
        var shorter = GlyphText.Measure("Речь", FontPx);
        var longer = GlyphText.Measure("Речь не распознана", FontPx);

        Assert.True(shorter > 0);
        Assert.True(longer > shorter);
    }

    [Theory]
    [InlineData("Резерв Groq (Gemini: лимит исчерпан)", 400f)]
    [InlineData("Очень длинное сообщение, которое никак не помещается в плашку шириной в сто пикселей", 100f)]
    [InlineData("Смайлы 😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀 и редкие символы ", 120f)]
    public void Build_FitsIntoMaxWidth_AndNeverFails(string text, float maxWidth)
    {
        var factory = CreateFactory();
        try
        {
            var path = GlyphText.Build(factory, text, FontPx, maxWidth, 18f, out var width);

            Assert.True(path is not null, "путь текста не построен");
            Assert.InRange(width, 1f, maxWidth);
            Native.Release((nint)path);
        }
        finally
        {
            Native.Release((nint)factory);
        }
    }

    [Fact]
    public void Build_ShortText_IsNotEllipsized()
    {
        var factory = CreateFactory();
        try
        {
            var path = GlyphText.Build(factory, "Речь не распознана", FontPx, 400f, 18f, out var width);

            Assert.Equal(GlyphText.Measure("Речь не распознана", FontPx), width, 3);
            Native.Release((nint)path);
        }
        finally
        {
            Native.Release((nint)factory);
        }
    }

    [Fact]
    public void FallbackFont_IsInstalled() =>
        Assert.True(GdiTextMask.IsFontAvailable(OverlayMotion.FallbackFontFace), "запасного шрифта Segoe UI нет в системе");

    private static ID2D1Factory* CreateFactory()
    {
        var iid = ID2D1Factory.IID_Guid;
        void* factory;
        Assert.True(Windows.Win32.Win32.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, &iid, null, &factory) >= 0);
        return (ID2D1Factory*)factory;
    }
}
