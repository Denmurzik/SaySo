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
                px[i] = alpha is null ? 0u : Straight(rgb, alpha[i] / 255f);
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
                            px[y * size + x] = Over(px[y * size + x], badge, c);
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

    // HICON из 32-битного растра ожидает непремультиплицированную альфу (как GDI+ GetHicon): иначе по краям тёмная кайма.
    private static uint Straight(uint rgb, float a) => ((uint)(a * 255 + 0.5f) << 24) | (rgb & 0xFFFFFF);

    /// <summary>Накладывает цвет <paramref name="rgb"/> с покрытием <paramref name="coverage"/> на непремультиплицированный пиксель.</summary>
    private static uint Over(uint dst, uint rgb, float coverage)
    {
        var dstA = (dst >> 24) / 255f;
        var outA = coverage + (dstA * (1 - coverage));
        if (outA <= 0f)
        {
            return 0;
        }

        uint Ch(int shift)
        {
            var src = (rgb >> shift) & 0xFF;
            var old = (dst >> shift) & 0xFF;
            return (uint)Math.Clamp((((src * coverage) + (old * dstA * (1 - coverage))) / outA) + 0.5f, 0f, 255f);
        }

        return ((uint)(outA * 255 + 0.5f) << 24) | (Ch(16) << 16) | (Ch(8) << 8) | Ch(0);
    }
}
