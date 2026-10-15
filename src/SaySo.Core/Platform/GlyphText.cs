using SaySo.Core.Interop;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace SaySo.Core.Platform;

/// <summary>
/// Однострочный текст плашки контурами глифов: GDI отдаёт контуры (без хинтинга) и продвижения, из них собирается
/// путь Direct2D — нужна только фабрика D2D, устройство D3D не создаётся (оно держало бы в процессе драйвер видеокарты,
/// +25 МБ). DWM рисует путь как фигуру со сглаживанием серым.
/// </summary>
internal static unsafe class GlyphText
{
    internal const string Face = "Segoe UI Variable Text";
    private const int Weight = 400; // обычное начертание
    private const int Oversample = 16; // контуры снимаются в 16× размере: дробные продвижения и точная форма
    private const char Ellipsis = '…';

    private static Native.MAT2 Identity => new() { eM11 = 0x10000, eM22 = 0x10000 };

    /// <summary>Ширина строки в пикселях (без многоточия). 0 — сбой GDI.</summary>
    public static float Measure(string text, int fontPx)
    {
        using var font = new FontScope(fontPx);
        return font.Ok ? font.Advances(font.Glyphs(text)).Sum() : 0f;
    }

    /// <summary>
    /// Путь строки: начало — x = 0, середина строки — <paramref name="centerY"/>. Длиннее <paramref name="maxWidth"/> —
    /// обрезается с многоточием. null — сбой GDI (плашка покажется без текста).
    /// </summary>
    public static ID2D1PathGeometry* Build(ID2D1Factory* factory, string text, int fontPx, float maxWidth, float centerY, out float width)
    {
        width = 0;
        using var font = new FontScope(fontPx);
        if (!font.Ok)
        {
            return null;
        }

        var glyphs = font.Glyphs(text);
        var advances = font.Advances(glyphs);
        var total = advances.Sum();
        if (total > maxWidth)
        {
            var ellipsis = font.Glyphs(Ellipsis.ToString());
            var ellipsisAdvance = font.Advances(ellipsis)[0];
            var n = glyphs.Length;
            while (n > 0 && total + ellipsisAdvance > maxWidth)
            {
                total -= advances[--n];
            }

            glyphs = [.. glyphs.AsSpan(0, n), ellipsis[0]];
            advances = [.. advances.AsSpan(0, n), ellipsisAdvance];
            total += ellipsisAdvance;
        }

        ID2D1PathGeometry* path;
        factory->CreatePathGeometry(&path);
        ID2D1GeometrySink* sink;
        path->Open(&sink);
        try
        {
            sink->SetFillMode(D2D1_FILL_MODE.D2D1_FILL_MODE_WINDING);
            var baseline = centerY - font.LineHeight / 2f + font.Ascent;
            var pen = 0f;
            for (var i = 0; i < glyphs.Length; i++)
            {
                font.Outline(glyphs[i], sink, pen, baseline);
                pen += advances[i];
            }

            sink->Close();
        }
        finally
        {
            Native.Release((nint)sink);
        }

        width = total;
        return path;
    }

    /// <summary>Контекст GDI с выбранным шрифтом плашки в <see cref="Oversample"/>× размере.</summary>
    private readonly struct FontScope : IDisposable
    {
        private readonly nint _dc;
        private readonly nint _font;
        private readonly nint _old;

        public FontScope(int fontPx)
        {
            _dc = Native.CreateCompatibleDC(0);
            _font = _dc == 0 ? 0 : Native.CreateFontW(-fontPx * Oversample, 0, 0, 0, Weight, 0, 0, 0, 1, 0, 0, Native.ANTIALIASED_QUALITY, 0, Face);
            _old = _font == 0 ? 0 : Native.SelectObject(_dc, _font);
            Native.TEXTMETRICW tm = default;
            Ok = _font != 0 && Native.GetTextMetricsW(_dc, &tm);
            Ascent = Ok ? tm.tmAscent / (float)Oversample : 0;
            LineHeight = Ok ? (tm.tmAscent + tm.tmDescent) / (float)Oversample : 0;
        }

        public bool Ok { get; }

        public float Ascent { get; }

        public float LineHeight { get; }

        public ushort[] Glyphs(string text)
        {
            var glyphs = new ushort[text.Length];
            fixed (char* p = text)
            fixed (ushort* g = glyphs)
            {
                Native.GetGlyphIndicesW(_dc, p, text.Length, g, 0);
            }

            return glyphs;
        }

        public float[] Advances(ushort[] glyphs)
        {
            var advances = new float[glyphs.Length];
            for (var i = 0; i < glyphs.Length; i++)
            {
                Native.GLYPHMETRICS gm;
                var m = Identity;
                advances[i] = Native.GetGlyphOutlineW(_dc, glyphs[i], Native.GGO_GLYPH_INDEX | Native.GGO_UNHINTED, &gm, 0, null, &m) == Native.GDI_ERROR
                    ? 0f
                    : gm.gmCellIncX / (float)Oversample;
            }

            return advances;
        }

        /// <summary>Контуры глифа (TTPOLYGONHEADER/TTPOLYCURVE, GGO_BEZIER: линии и кубические кривые) → фигуры пути.</summary>
        public void Outline(ushort glyph, ID2D1GeometrySink* sink, float x0, float baseline)
        {
            Native.GLYPHMETRICS gm;
            var m = Identity;
            const uint Format = Native.GGO_BEZIER | Native.GGO_GLYPH_INDEX | Native.GGO_UNHINTED;
            var size = Native.GetGlyphOutlineW(_dc, glyph, Format, &gm, 0, null, &m);
            if (size == 0 || size == Native.GDI_ERROR)
            {
                return; // пробел или сбой
            }

            var buffer = new byte[size];
            fixed (byte* b = buffer)
            {
                if (Native.GetGlyphOutlineW(_dc, glyph, Format, &gm, size, b, &m) == Native.GDI_ERROR)
                {
                    return;
                }

                // FIXED 16.16 в координатах глифа (Y вверх) → пиксели плашки (Y вниз).
                D2D_POINT_2F P(byte* at) => new()
                {
                    x = x0 + *(int*)at / 65536f / Oversample,
                    y = baseline - *(int*)(at + 4) / 65536f / Oversample,
                };

                var o = 0u;
                while (o + 16 <= size)
                {
                    var header = b + o;
                    var cb = *(uint*)header;
                    sink->BeginFigure(P(header + 8), D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
                    var c = header + 16;
                    while (c + 4 <= header + cb)
                    {
                        var type = *(ushort*)c;
                        var count = *(ushort*)(c + 2);
                        var points = c + 4;
                        if (type == 1) // TT_PRIM_LINE
                        {
                            for (var k = 0; k < count; k++)
                            {
                                sink->AddLine(P(points + k * 8));
                            }
                        }
                        else // TT_PRIM_CSPLINE: тройки «контрольная, контрольная, конец»
                        {
                            for (var k = 0; k + 2 < count; k += 3)
                            {
                                var segment = new D2D1_BEZIER_SEGMENT
                                {
                                    point1 = P(points + k * 8),
                                    point2 = P(points + (k + 1) * 8),
                                    point3 = P(points + (k + 2) * 8),
                                };
                                sink->AddBezier(&segment);
                            }
                        }

                        c = points + count * 8;
                    }

                    sink->EndFigure(D2D1_FIGURE_END.D2D1_FIGURE_END_CLOSED);
                    o += cb;
                }
            }
        }

        public void Dispose()
        {
            if (_old != 0)
            {
                Native.SelectObject(_dc, _old);
            }

            if (_font != 0)
            {
                Native.DeleteObject(_font);
            }

            if (_dc != 0)
            {
                Native.DeleteDC(_dc);
            }
        }
    }
}
