using System.Diagnostics.CodeAnalysis;

namespace SaySo.Core.Overlay;

/// <summary>Рисует «пилюлю» оверлея в буфер BGRA с предумноженной альфой. Без GDI — чистая математика.</summary>
public static class OverlayRenderer
{
    public const int BarCount = 7;

    private const float BaseHeight = 36f;
    private const float BaseWidth = 112f;
    private const float BarWidth = 4f;
    private const float BarGap = 4f;
    private const float BarMin = 4f;
    private const float BarMax = 20f;
    private const float Pad = 16f;
    private const float DotSize = 8f;
    private const float TextGap = 8f;
    private const float MaxWidth = 480f;
    private const float BackgroundAlpha = 0.88f;
    private const float BorderAlpha = 0.10f;

    /// <remarks>
    /// Ширину текстовой маски вызывающий ограничивает сам (<c>maxWidthPx ≤ 480·scale − 48·scale</c>, см.
    /// <see cref="GdiTextMask.RenderText"/>), иначе маска будет жёстко обрезана по правому отступу.
    /// </remarks>
    public static (int Width, int Height) Measure(OverlayFrame frame, float scale)
    {
        var height = (int)MathF.Round(BaseHeight * scale);
        if (frame.Mode != OverlayMode.Message || !HasText(frame.Text))
        {
            return ((int)MathF.Round(BaseWidth * scale), height);
        }

        var width = (Pad + DotSize + TextGap + Pad) * scale + frame.Text.Width;
        return ((int)MathF.Round(MathF.Min(width, MaxWidth * scale)), height);
    }

    public static void Render(Span<uint> px, int width, int height, OverlayFrame frame, float scale)
    {
        if (px.Length != width * height)
        {
            throw new ArgumentException("Размер буфера не совпадает с width * height.", nameof(px));
        }

        px.Clear();
        DrawPill(px, width, height, scale);
        switch (frame.Mode)
        {
            case OverlayMode.Recording:
                DrawBars(px, width, height, frame.Levels, scale);
                break;
            case OverlayMode.Processing:
                DrawDots(px, width, height, frame.TimeMs, scale);
                break;
            case OverlayMode.Message:
                DrawMessage(px, width, height, frame, scale);
                break;
        }
    }

    private static void DrawPill(Span<uint> px, int w, int h, float scale)
    {
        var radius = h / 2f;
        var border = MathF.Max(1f, scale);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var d = RoundRectDistance(x + 0.5f, y + 0.5f, w, h, radius);
                var outer = Clamp01(0.5f - d);
                if (outer <= 0f)
                {
                    continue;
                }

                var inner = Clamp01(0.5f - (d + border));
                var fill = Premultiplied(0x16 / 255f, 0x16 / 255f, 0x18 / 255f, BackgroundAlpha * outer);
                var ring = Premultiplied(1f, 1f, 1f, BorderAlpha * (outer - inner));
                px[y * w + x] = Over(ring, fill);
            }
        }
    }

    private static void DrawBars(Span<uint> px, int w, int h, IReadOnlyList<float> levels, float scale)
    {
        var barW = BarWidth * scale;
        var gap = BarGap * scale;
        var total = BarCount * barW + (BarCount - 1) * gap;
        var x0 = (w - total) / 2f;
        for (var i = 0; i < BarCount; i++)
        {
            var level = i < levels.Count ? Clamp01(levels[i]) : 0f;
            var barH = (BarMin + level * (BarMax - BarMin)) * scale;
            var left = x0 + i * (barW + gap);
            var top = (h - barH) / 2f;
            FillRect(px, w, h, left, top, left + barW, top + barH, Premultiplied(1f, 1f, 1f, 0.95f));
        }
    }

    private static void DrawDots(Span<uint> px, int w, int h, long timeMs, float scale)
    {
        var step = 12f * scale;
        var r = 3f * scale;
        for (var i = 0; i < 3; i++)
        {
            var phase = (timeMs % 900) / 900f * MathF.Tau - i * 0.9f;
            var alpha = 0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(phase));
            FillCircle(px, w, h, w / 2f + (i - 1) * step, h / 2f, r, Premultiplied(1f, 1f, 1f, alpha));
        }
    }

    private static void DrawMessage(Span<uint> px, int w, int h, OverlayFrame frame, float scale)
    {
        var (r, g, b) = frame.Tone switch
        {
            OverlayTone.Error => (0xE8 / 255f, 0x4B / 255f, 0x4B / 255f),
            OverlayTone.Info => (0xF2 / 255f, 0xB3 / 255f, 0x3D / 255f),
            _ => (1f, 1f, 1f),
        };
        var dotR = DotSize * scale / 2f;
        FillCircle(px, w, h, Pad * scale + dotR, h / 2f, dotR, Premultiplied(r, g, b, 1f));

        if (frame.Text is not { } text || !HasText(text))
        {
            return;
        }

        var tx = (int)MathF.Round((Pad + DotSize + TextGap) * scale);
        var ty = (h - text.Height) / 2;
        var maxX = w - (int)MathF.Round(Pad * scale);
        for (var y = 0; y < text.Height; y++)
        {
            var py = ty + y;
            if (py < 0 || py >= h)
            {
                continue;
            }

            for (var x = 0; x < text.Width && tx + x < maxX; x++)
            {
                var a = text.Alpha[y * text.Width + x];
                if (a == 0)
                {
                    continue;
                }

                var i = py * w + tx + x;
                px[i] = Over(Premultiplied(1f, 1f, 1f, a / 255f * 0.95f), px[i]);
            }
        }
    }

    private static void FillRect(Span<uint> px, int w, int h, float left, float top, float right, float bottom, uint color)
    {
        for (var y = Math.Max(0, (int)top); y < Math.Min(h, (int)MathF.Ceiling(bottom)); y++)
        {
            var cy = Coverage(y, top, bottom);
            for (var x = Math.Max(0, (int)left); x < Math.Min(w, (int)MathF.Ceiling(right)); x++)
            {
                var c = cy * Coverage(x, left, right);
                if (c > 0f)
                {
                    px[y * w + x] = Over(Scale(color, c), px[y * w + x]);
                }
            }
        }
    }

    private static void FillCircle(Span<uint> px, int w, int h, float cx, float cy, float r, uint color)
    {
        for (var y = Math.Max(0, (int)(cy - r - 1)); y < Math.Min(h, (int)(cy + r + 2)); y++)
        {
            for (var x = Math.Max(0, (int)(cx - r - 1)); x < Math.Min(w, (int)(cx + r + 2)); x++)
            {
                var dx = x + 0.5f - cx;
                var dy = y + 0.5f - cy;
                var c = Clamp01(r - MathF.Sqrt(dx * dx + dy * dy) + 0.5f);
                if (c > 0f)
                {
                    px[y * w + x] = Over(Scale(color, c), px[y * w + x]);
                }
            }
        }
    }

    // Пустая маска (сбой GDI) или несогласованный размер буфера — «текста нет».
    private static bool HasText([NotNullWhen(true)] TextMask? mask) =>
        mask is { Width: > 0, Height: > 0 } && mask.Alpha.Length >= mask.Width * mask.Height;

    // Доля пикселя [p, p+1), покрытая отрезком [a, b].
    private static float Coverage(int p, float a, float b) => Clamp01(MathF.Min(p + 1, b) - MathF.Max(p, a));

    // Знаковое расстояние до скруглённого прямоугольника w×h с радиусом r (отрицательное внутри).
    private static float RoundRectDistance(float x, float y, float w, float h, float r)
    {
        var qx = MathF.Abs(x - w / 2f) - (w / 2f - r);
        var qy = MathF.Abs(y - h / 2f) - (h / 2f - r);
        var ox = MathF.Max(qx, 0f);
        var oy = MathF.Max(qy, 0f);
        return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - r;
    }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    private static uint Premultiplied(float r, float g, float b, float a)
    {
        a = Clamp01(a);
        return ((uint)(a * 255f + 0.5f) << 24)
               | ((uint)(r * a * 255f + 0.5f) << 16)
               | ((uint)(g * a * 255f + 0.5f) << 8)
               | (uint)(b * a * 255f + 0.5f);
    }

    private static uint Scale(uint color, float k)
    {
        uint Ch(int shift) => (uint)(((color >> shift) & 0xFF) * k + 0.5f);
        return (Ch(24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }

    // Наложение предумноженных цветов: out = src + dst × (1 − srcA).
    private static uint Over(uint src, uint dst)
    {
        var sa = src >> 24;
        if (sa == 255)
        {
            return src;
        }

        if (sa == 0)
        {
            return dst;
        }

        var inv = 255 - sa;
        uint Ch(int shift) => ((src >> shift) & 0xFF) + ((((dst >> shift) & 0xFF) * inv + 127) / 255);
        return (Ch(24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }
}
