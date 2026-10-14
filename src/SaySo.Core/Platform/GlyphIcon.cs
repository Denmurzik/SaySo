using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;
using SaySo.Core.Overlay;

namespace SaySo.Core.Platform;

/// <summary>Значок из глифа Segoe Fluent Icons заданного цвета, с необязательной цветной точкой-индикатором.</summary>
internal static unsafe class GlyphIcon
{
    /// <returns>HICON или 0, если GDI не смог создать значок (причина — в журнале).</returns>
    public static nint Create(char glyph, int size, uint rgb, uint? badgeRgb)
    {
        if (size <= 0)
        {
            return 0;
        }

        var mask = GdiTextMask.RenderGlyph(glyph, "Segoe Fluent Icons", size);
        // Пустая или неподходящая по размеру маска (сбой GDI) — прозрачный глиф; точка-индикатор всё равно рисуется.
        var alpha = mask.Width == size && mask.Height == size && mask.Alpha.Length >= size * size ? mask.Alpha : null;

        var screen = Native.GetDC(0);
        if (screen == 0)
        {
            Fail("GetDC");
            return 0;
        }

        nint color = 0;
        nint mono = 0;
        try
        {
            var info = Native.TopDown32(size, size);
            void* bits = null;
            color = Native.CreateDIBSection(screen, &info, 0, &bits, 0, 0);
            if (color == 0 || bits == null)
            {
                Fail("CreateDIBSection");
                return 0;
            }

            var px = (uint*)bits;
            for (var i = 0; i < size * size; i++)
            {
                px[i] = alpha is null ? 0u : Premultiplied(rgb, alpha[i] / 255f);
            }

            if (badgeRgb is { } badge)
            {
                var cx = size * 0.76f;
                var cy = size * 0.76f;
                var r = size * 0.22f;
                for (var y = 0; y < size; y++)
                {
                    for (var x = 0; x < size; x++)
                    {
                        var dx = x + 0.5f - cx;
                        var dy = y + 0.5f - cy;
                        var c = Math.Clamp(r - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0f, 1f);
                        if (c > 0f)
                        {
                            px[y * size + x] = Lerp(px[y * size + x], Premultiplied(badge, 1f), c);
                        }
                    }
                }
            }

            var maskBits = new byte[(size + 15) / 16 * 2 * size]; // 1 бит/пиксель, все нули — прозрачность берётся из альфы
            fixed (byte* m = maskBits)
            {
                mono = Native.CreateBitmap(size, size, 1, 1, m);
            }

            if (mono == 0)
            {
                Fail("CreateBitmap");
                return 0;
            }

            var iconInfo = new Native.ICONINFO { fIcon = 1, hbmMask = mono, hbmColor = color };
            var icon = Native.CreateIconIndirect(&iconInfo);
            if (icon == 0)
            {
                Fail("CreateIconIndirect");
            }

            return icon;
        }
        finally
        {
            // Растры после CreateIconIndirect копируются в значок, при сбое — освобождаются здесь же.
            if (color != 0)
            {
                Native.DeleteObject(color);
            }

            if (mono != 0)
            {
                Native.DeleteObject(mono);
            }

            Native.ReleaseDC(0, screen);
        }
    }

    private static void Fail(string call) => Log.Info($"GlyphIcon: сбой {call}, значок не создан");

    private static uint Premultiplied(uint rgb, float a)
    {
        var r = (rgb >> 16) & 0xFF;
        var g = (rgb >> 8) & 0xFF;
        var b = rgb & 0xFF;
        return ((uint)(a * 255 + 0.5f) << 24) | ((uint)(r * a + 0.5f) << 16) | ((uint)(g * a + 0.5f) << 8) | (uint)(b * a + 0.5f);
    }

    private static uint Lerp(uint from, uint to, float t)
    {
        uint Ch(int shift) => (uint)((((from >> shift) & 0xFF) * (1 - t)) + (((to >> shift) & 0xFF) * t) + 0.5f);
        return (Ch(24) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }
}
