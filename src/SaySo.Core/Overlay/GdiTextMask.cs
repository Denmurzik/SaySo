using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;

namespace SaySo.Core.Overlay;

/// <summary>
/// Растеризация текста/глифа через GDI в маску покрытия (белое на чёрном → значение канала = альфа).
/// При любом сбое GDI возвращает пустую маску 0×0 (никогда не бросает и не пишет в нулевую память).
/// </summary>
public static unsafe class GdiTextMask
{
    private const string TextFont = "Segoe UI Variable Text";

    /// <summary>
    /// Растеризует однострочный текст. Контракт: вызывающий передаёт <paramref name="maxWidthPx"/> не больше
    /// 480·scale − 48·scale (максимальная ширина оверлея минус отступы и точка), тогда многоточие ставит GDI;
    /// иначе <see cref="OverlayRenderer"/> жёстко обрежет маску по правому отступу.
    /// </summary>
    public static TextMask RenderText(string text, int fontPx, int maxWidthPx)
    {
        return Render(TextFont, fontPx, Native.FW_SEMIBOLD, dc =>
        {
            Native.SIZE size = default;
            bool ok;
            fixed (char* p = text)
            {
                ok = Native.GetTextExtentPoint32W(dc, p, text.Length, &size);
            }

            return ok ? (Math.Min(size.cx + 2, maxWidthPx), size.cy + 2) : null;
        }, (dc, w, h) =>
        {
            var rect = new Native.RECT { Left = 1, Top = 1, Right = w - 1, Bottom = h };
            fixed (char* p = text)
            {
                Native.DrawTextW(dc, p, text.Length, &rect, Native.DT_LEFT | Native.DT_SINGLELINE | Native.DT_NOPREFIX | Native.DT_END_ELLIPSIS);
            }
        });
    }

    /// <summary>
    /// Имя шрифта, который GDI реально выберет для текста плашки. Если «Segoe UI Variable Text» не установлен (Windows 10,
    /// серверные раннеры CI), GDI подставляет другой шрифт — тогда ширины строк отличаются от рассчитанных.
    /// Пустая строка — сбой GDI.
    /// </summary>
    internal static string ResolvedTextFace(string face = TextFont)
    {
        var dc = Native.CreateCompatibleDC(0);
        if (dc == 0)
        {
            return "";
        }

        try
        {
            var font = Native.CreateFontW(-13, 0, 0, 0, Native.FW_SEMIBOLD, 0, 0, 0, 1, 0, 0, Native.ANTIALIASED_QUALITY, 0, face);
            if (font == 0)
            {
                return "";
            }

            var old = Native.SelectObject(dc, font);
            try
            {
                var buffer = stackalloc char[64];
                var length = Native.GetTextFaceW(dc, 64, buffer);
                return length > 1 ? new string(buffer, 0, length - 1) : ""; // length включает завершающий ноль
            }
            finally
            {
                Native.SelectObject(dc, old);
                Native.DeleteObject(font);
            }
        }
        finally
        {
            Native.DeleteDC(dc);
        }
    }

    /// <summary>Установлен ли шрифт плашки (GDI не подставил другой).</summary>
    internal static bool IsTextFontAvailable() => ResolvedTextFace().StartsWith(TextFont, StringComparison.OrdinalIgnoreCase);

    public static TextMask RenderGlyph(char glyph, string fontFace, int sizePx)
    {
        return Render(fontFace, sizePx, Native.FW_NORMAL, _ => (sizePx, sizePx), (dc, w, h) =>
        {
            var rect = new Native.RECT { Left = 0, Top = 0, Right = w, Bottom = h };
            var s = glyph;
            Native.DrawTextW(dc, &s, 1, &rect, Native.DT_CENTER | Native.DT_VCENTER | Native.DT_SINGLELINE | Native.DT_NOPREFIX);
        });
    }

    private static TextMask Fail(string what)
    {
        // Текст диктовки в журнал не попадает — только факт сбоя.
        Log.Info($"GdiTextMask: сбой GDI ({what}), возвращена пустая маска");
        return new TextMask([], 0, 0);
    }

    private static TextMask Render(
        string face,
        int fontPx,
        int weight,
        Func<nint, (int W, int H)?> measure,
        Action<nint, int, int> draw)
    {
        var dc = Native.CreateCompatibleDC(0);
        if (dc == 0)
        {
            return Fail("CreateCompatibleDC");
        }

        try
        {
            var font = Native.CreateFontW(-fontPx, 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, Native.ANTIALIASED_QUALITY, 0, face);
            if (font == 0)
            {
                return Fail("CreateFontW");
            }

            var oldFont = Native.SelectObject(dc, font);
            try
            {
                if (measure(dc) is not { } size)
                {
                    return Fail("GetTextExtentPoint32W");
                }

                var w = Math.Max(1, size.W);
                var h = Math.Max(1, size.H);
                var info = Native.TopDown32(w, h);
                void* bits = null;
                var dib = Native.CreateDIBSection(dc, &info, 0, &bits, 0, 0);
                if (dib == 0 || bits == null)
                {
                    if (dib != 0)
                    {
                        Native.DeleteObject(dib);
                    }

                    return Fail("CreateDIBSection");
                }

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
            }
        }
        finally
        {
            Native.DeleteDC(dc);
        }
    }
}
