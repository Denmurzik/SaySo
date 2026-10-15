using System.Text;
using SaySo.Core.Interop;
using SaySo.Core.Overlay;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;

namespace SaySo.Core.Platform;

/// <summary>
/// Однострочный текст плашки контурами глифов: GDI отдаёт контуры (без хинтинга) и продвижения, из них собирается
/// путь Direct2D — нужна только фабрика D2D, устройство D3D не создаётся (оно держало бы в процессе драйвер видеокарты,
/// +25 МБ). DWM рисует путь как фигуру со сглаживанием серым.
/// </summary>
/// <remarks>
/// Шрифт — <see cref="OverlayMotion.FontFace"/>, а если его нет (Windows 10) — <see cref="OverlayMotion.FallbackFontFace"/>.
/// Символы, которых нет в основном шрифте, берутся из запасного; символы вне BMP (суррогатные пары) GDI без Uniscribe
/// не отображает — на пару ставится один пустой глиф (.notdef), пара не разрывается.
/// </remarks>
internal static unsafe class GlyphText
{
    private const int Oversample = 16; // контуры снимаются в 16× размере: дробные продвижения и точная форма
    private const ushort Missing = 0xFFFF;
    private const uint GGI_MARK_NONEXISTING_GLYPHS = 1;

    private static readonly Lazy<string> PrimaryFace = new(() =>
        GdiTextMask.IsFontAvailable(OverlayMotion.FontFace) ? OverlayMotion.FontFace : OverlayMotion.FallbackFontFace);

    private static Native.MAT2 Identity => new() { eM11 = 0x10000, eM22 = 0x10000 };

    /// <summary>Глиф строки: индекс в шрифте (0 — основной, 1 — запасной), продвижение и пробел ли это.</summary>
    private readonly record struct Glyph(ushort Index, int Font, float Advance, bool Space);

    /// <summary>Ширина строки в пикселях (без многоточия). 0 — сбой GDI.</summary>
    public static float Measure(string text, int fontPx)
    {
        using var fonts = new Fonts(fontPx);
        return fonts.Ok ? Shape(fonts, text).Sum(g => g.Advance) : 0f;
    }

    /// <summary>
    /// Путь строки: начало — x = 0, середина строки — <paramref name="centerY"/>. Длиннее <paramref name="maxWidth"/> —
    /// обрезается с многоточием (<see cref="OverlayMotion.FitText"/>). null — сбой GDI или D2D (плашка покажется без текста).
    /// </summary>
    public static ID2D1PathGeometry* Build(ID2D1Factory* factory, string text, int fontPx, float maxWidth, float centerY, out float width)
    {
        width = 0;
        using var fonts = new Fonts(fontPx);
        if (!fonts.Ok)
        {
            return null;
        }

        var glyphs = Shape(fonts, text);
        var ellipsis = Shape(fonts, "…")[0];
        var (keep, cut) = OverlayMotion.FitText(
            glyphs.Select(g => g.Advance).ToArray(), glyphs.Select(g => g.Space).ToArray(), ellipsis.Advance, maxWidth);
        if (cut)
        {
            glyphs = [.. glyphs.AsSpan(0, keep), ellipsis];
        }

        ID2D1PathGeometry* path;
        var hr = (*(ID2D1Factory.Vtbl**)factory)->CreatePathGeometry_11(factory, &path);
        if (hr.Failed)
        {
            PlatformLog.Info($"ID2D1Factory.CreatePathGeometry: 0x{hr.Value:X8}");
            return null;
        }

        ID2D1GeometrySink* sink;
        hr = (*(ID2D1PathGeometry.Vtbl**)path)->Open_18(path, &sink);
        if (hr.Failed)
        {
            PlatformLog.Info($"ID2D1PathGeometry.Open: 0x{hr.Value:X8}");
            Native.Release((nint)path);
            return null;
        }

        sink->SetFillMode(D2D1_FILL_MODE.D2D1_FILL_MODE_WINDING);
        var baseline = centerY - fonts.LineHeight / 2f + fonts.Ascent;
        var pen = 0f;
        foreach (var glyph in glyphs)
        {
            fonts.Outline(glyph, sink, pen, baseline);
            pen += glyph.Advance;
        }

        hr = (*(ID2D1GeometrySink.Vtbl**)sink)->Close_10(sink);
        Native.Release((nint)sink);
        if (hr.Failed)
        {
            PlatformLog.Info($"ID2D1GeometrySink.Close: 0x{hr.Value:X8}");
            Native.Release((nint)path);
            return null;
        }

        width = pen;
        return path;
    }

    /// <summary>Глифы строки по символам Юникода (руны): основной шрифт, затем запасной, иначе пустой глиф.</summary>
    private static Glyph[] Shape(Fonts fonts, string text)
    {
        var glyphs = new List<Glyph>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var (index, font) = (Missing, 0);
            if (rune.IsBmp)
            {
                index = fonts.Index(0, (char)rune.Value);
                if (index == Missing)
                {
                    (index, font) = (fonts.Index(1, (char)rune.Value), 1);
                }
            }

            if (index == Missing)
            {
                (index, font) = (0, 0); // .notdef основного шрифта
            }

            glyphs.Add(new Glyph(index, font, fonts.Advance(font, index), Rune.IsWhiteSpace(rune)));
        }

        return [.. glyphs];
    }

    /// <summary>Основной и (по требованию) запасной шрифт плашки в <see cref="Oversample"/>× размере.</summary>
    private sealed class Fonts : IDisposable
    {
        private readonly int _fontPx;
        private readonly Face _primary;
        private Face? _fallback;

        public Fonts(int fontPx)
        {
            _fontPx = fontPx;
            _primary = new Face(PrimaryFace.Value, fontPx);
        }

        public bool Ok => _primary.Ok;

        public float Ascent => _primary.Ascent;

        public float LineHeight => _primary.LineHeight;

        private Face At(int font) => font == 0 ? _primary : _fallback ??= new Face(OverlayMotion.FallbackFontFace, _fontPx);

        public ushort Index(int font, char c)
        {
            var face = At(font);
            return face.Ok ? face.Index(c) : Missing;
        }

        public float Advance(int font, ushort index) => At(font).Advance(index);

        public void Outline(Glyph glyph, ID2D1GeometrySink* sink, float x0, float baseline) =>
            At(glyph.Font).Outline(glyph.Index, sink, x0, baseline);

        public void Dispose()
        {
            _primary.Dispose();
            _fallback?.Dispose();
        }
    }

    /// <summary>Контекст GDI с выбранным шрифтом.</summary>
    private sealed class Face : IDisposable
    {
        private readonly nint _dc;
        private readonly nint _font;
        private readonly nint _old;

        public Face(string face, int fontPx)
        {
            _dc = Native.CreateCompatibleDC(0);
            _font = _dc == 0 ? 0 : Native.CreateFontW(-fontPx * Oversample, 0, 0, 0, OverlayMotion.FontWeight, 0, 0, 0, 1, 0, 0,
                Native.ANTIALIASED_QUALITY, 0, face);
            _old = _font == 0 ? 0 : Native.SelectObject(_dc, _font);
            Native.TEXTMETRICW tm = default;
            Ok = _font != 0 && Native.GetTextMetricsW(_dc, &tm);
            Ascent = Ok ? tm.tmAscent / (float)Oversample : 0;
            LineHeight = Ok ? (tm.tmAscent + tm.tmDescent) / (float)Oversample : 0;
        }

        public bool Ok { get; }

        public float Ascent { get; }

        public float LineHeight { get; }

        public ushort Index(char c)
        {
            ushort index;
            return Native.GetGlyphIndicesW(_dc, &c, 1, &index, GGI_MARK_NONEXISTING_GLYPHS) == Native.GDI_ERROR ? Missing : index;
        }

        public float Advance(ushort index)
        {
            if (!Ok)
            {
                return 0f;
            }

            Native.GLYPHMETRICS gm;
            var m = Identity;
            return Native.GetGlyphOutlineW(_dc, index, Native.GGO_GLYPH_INDEX | Native.GGO_UNHINTED, &gm, 0, null, &m) == Native.GDI_ERROR
                ? 0f
                : gm.gmCellIncX / (float)Oversample;
        }

        /// <summary>Контуры глифа (TTPOLYGONHEADER/TTPOLYCURVE, GGO_BEZIER: линии и кубические кривые) → фигуры пути.</summary>
        public void Outline(ushort glyph, ID2D1GeometrySink* sink, float x0, float baseline)
        {
            if (!Ok)
            {
                return;
            }

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
                    if (cb < 16 || o + cb > size)
                    {
                        return; // повреждённые данные: остаток глифа не рисуем
                    }

                    sink->BeginFigure(P(header + 8), D2D1_FIGURE_BEGIN.D2D1_FIGURE_BEGIN_FILLED);
                    var c = header + 16;
                    while (c + 4 <= header + cb)
                    {
                        var type = *(ushort*)c;
                        var count = *(ushort*)(c + 2);
                        var points = c + 4;
                        if (points + count * 8 > header + cb)
                        {
                            break;
                        }

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
