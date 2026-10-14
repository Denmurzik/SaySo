using SaySo.Core.Interop;

namespace SaySo.Core.Overlay;

/// <summary>Растеризация текста/глифа через GDI в маску покрытия (белое на чёрном → значение канала = альфа).</summary>
public static unsafe class GdiTextMask
{
    private const string TextFont = "Segoe UI Variable Text";

    public static TextMask RenderText(string text, int fontPx, int maxWidthPx)
    {
        return Render(TextFont, fontPx, Native.FW_SEMIBOLD, dc =>
        {
            Native.SIZE size;
            fixed (char* p = text)
            {
                Native.GetTextExtentPoint32W(dc, p, text.Length, &size);
            }

            return (Math.Min(size.cx + 2, maxWidthPx), size.cy + 2);
        }, (dc, w, h) =>
        {
            var rect = new Native.RECT { Left = 1, Top = 1, Right = w - 1, Bottom = h };
            fixed (char* p = text)
            {
                Native.DrawTextW(dc, p, text.Length, &rect, Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX | Native.DT_END_ELLIPSIS);
            }
        });
    }

    public static TextMask RenderGlyph(char glyph, string fontFace, int sizePx)
    {
        return Render(fontFace, sizePx, Native.FW_NORMAL, _ => (sizePx, sizePx), (dc, w, h) =>
        {
            var rect = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            var s = glyph;
            Native.DrawTextW(dc, &s, 1, &rect, Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
        });
    }

    private static TextMask Render(
        string face,
        int fontPx,
        int weight,
        Func<nint, (int W, int H)> measure,
        Action<nint, int, int> draw)
    {
        var dc = Native.CreateCompatibleDC(0);
        var font = Native.CreateFontW(-fontPx, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, Native.ANTIALIASED_QUALITY, 0, face);
        var oldFont = Native.SelectObject(dc, font);
        try
        {
            var (w, h) = measure(dc);
            w = Math.Max(1, w);
            h = Math.Max(1, h);
            var info = Native.TopDown32(w, h);
            void* bits;
            var dib = Native.CreateDIBSection(dc, &info, 0, &bits, 0, 0);
            var oldBitmap = Native.SelectObject(dc, dib);
            try
            {
                new Span<uint>(bits, w * h).Clear();
                Native.SetBkMode(dc, Native.TRANSPARENT);
                Native.SetTextColor(dc, 0x00FFFFFF);
                draw(dc, w, h);
                Native.GdiFlush();

                var alpha = new byte[w * h];
                var pixels = (uint*)bits;
                for (var i = 0; i < alpha.Length; i++)
                {
                    alpha[i] = (byte)((pixels[i] >> 8) & 0xFF); // зелёный канал (без ClearType все каналы равны)
                }

                return new TextMask(alpha, w, h);
            }
            finally
            {
                Native.SelectObject(dc, oldBitmap);
                Native.DeleteObject(dib);
            }
        }
        finally
        {
            Native.SelectObject(dc, oldFont);
            Native.DeleteObject(font);
            Native.DeleteDC(dc);
        }
    }
}
