using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using SaySo.Core.Overlay;

namespace SaySo.Probe;

/// <summary>
/// <c>overlay-capture --exe SaySo.exe [--out-dir D]</c>: запись настоящей плашки с экрана для README. Под область плашки
/// кладётся своё окно-подложка с нейтральным пёстрым рисунком (без личного содержимого экрана, Acrylic виден),
/// затем запускается <c>SaySo.exe --overlay-demo</c> и область снимается с экрана, пока демо не закончится.
/// Результат: overlay-demo.png (APNG по отметкам времени демо) и кадры состояний overlay-*.png.
/// </summary>
internal static unsafe partial class OverlayCapture
{
    private const int Fps = 40;

    public static int Run(string exe, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var layout = CurrentLayout();
        var m = OverlayMotion.Px(28, layout.Scale);
        var area = new RECT
        {
            Left = layout.WindowLeft + layout.CapsuleLeft - m,
            Top = layout.WindowTop - m,
            Right = layout.WindowLeft + layout.CapsuleLeft + layout.CapsuleMaxWidth + m,
            Bottom = layout.WindowTop + layout.WindowHeight, // до низа экрана: виден выезд из-за края
        };

        // Подложка шире области записи: размытие берёт цвета и из-за краёв капсулы.
        var pad = OverlayMotion.Px(60, layout.Scale);
        _capsuleX = layout.WindowLeft + layout.CapsuleLeft + layout.CapsuleMaxWidth / 2f - (area.Left - pad);
        _capsuleY = layout.WindowTop + layout.EndY + layout.CapsuleHeight / 2f - (area.Top - pad);
        _unit = layout.CapsuleHeight;
        var backdrop = CreateBackdrop(area.Left - pad, area.Top - pad, area.Right - area.Left + 2 * pad, area.Bottom - area.Top + 2 * pad);
        timeBeginPeriod(1); // Sleep(1) по 1 мс, а не по 15,6: ровный шаг кадров
        try
        {
            Pump(400);
            var still = Grab(area);
            var report = Path.GetFullPath(Path.Combine(outDir, "overlay-demo.txt"));
            using var demo = Process.Start(new ProcessStartInfo(exe, ["--overlay-demo", "--out", report]) { UseShellExecute = false })
                             ?? throw new InvalidOperationException("SaySo.exe не запустился");
            var frames = new List<uint[]>();
            var ticks = new List<long>();
            while (!demo.HasExited)
            {
                var t = Environment.TickCount64;
                frames.Add(Grab(area));
                ticks.Add(t);
                Pump(Math.Max(1, (int)(1000 / Fps - (Environment.TickCount64 - t))));
            }

            if (demo.ExitCode != 0)
            {
                Console.WriteLine($"Демо завершилось с кодом {demo.ExitCode} (освобождений меньше ожидаемого)");
            }

            var states = File.ReadAllLines(report)
                .Where(l => l.StartsWith("state ", StringComparison.Ordinal))
                .Select(l => l.Split(' '))
                .ToDictionary(p => p[1], p => long.Parse(p[2]["tick=".Length..], CultureInfo.InvariantCulture));
            var w = area.Right - area.Left;
            var h = area.Bottom - area.Top;

            // Анимация: от появления до конца автоскрытия сообщения, без хвоста пустого фона.
            var first = ticks.FindIndex(t => t >= states["recording"] - 150);
            var last = ticks.FindLastIndex(t => t <= states["hidden"] + 250);
            var clip = frames.GetRange(first, last - first + 1);
            var delays = Enumerable.Range(first, last - first + 1)
                .Select(i => (int)((i + 1 < ticks.Count ? ticks[i + 1] : ticks[i] + 33) - ticks[i])).ToList();
            delays[^1] = 1200; // пауза перед повтором
            var anim = Path.Combine(outDir, "overlay-demo.png");
            ApngWriter.WriteTimed(anim, w, h, clip, delays);
            Console.WriteLine($"OK {anim} ({w}×{h}, кадров {clip.Count}, {new FileInfo(anim).Length / 1024} КБ)");

            foreach (var (file, state, afterMs) in new[]
                     {
                         ("overlay-recording.png", "recording", 1500),
                         ("overlay-processing.png", "processing", 900),
                         ("overlay-error.png", "error", 1300),
                         ("overlay-info.png", "info", 1300),
                     })
            {
                var at = ticks.FindIndex(t => t >= states[state] + afterMs);
                SaveCapsule(Path.Combine(outDir, file), frames[at], still, w, h, OverlayMotion.Px(10, layout.Scale));
            }
        }
        finally
        {
            timeEndPeriod(1);
            DestroyWindow(backdrop);
        }

        return 0;
    }

    /// <summary>Кадр, обрезанный по капсуле (пиксели, отличные от подложки) с полями <paramref name="margin"/>.</summary>
    private static void SaveCapsule(string path, uint[] frame, uint[] background, int w, int h, int margin)
    {
        int x0 = w, y0 = h, x1 = 0, y1 = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var a = frame[y * w + x];
                var b = background[y * w + x];
                var d = Math.Abs((int)(a & 0xFF) - (int)(b & 0xFF)) + Math.Abs((int)((a >> 8) & 0xFF) - (int)((b >> 8) & 0xFF))
                        + Math.Abs((int)((a >> 16) & 0xFF) - (int)((b >> 16) & 0xFF));
                if (d > 12)
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x + 1);
                    y1 = Math.Max(y1, y + 1);
                }
            }
        }

        x0 = Math.Max(0, x0 - margin);
        y0 = Math.Max(0, y0 - margin);
        x1 = Math.Min(w, x1 + margin);
        y1 = Math.Min(h, y1 + margin);
        var cw = x1 - x0;
        var ch = y1 - y0;
        var crop = new uint[cw * ch];
        for (var y = 0; y < ch; y++)
        {
            frame.AsSpan((y0 + y) * w + x0, cw).CopyTo(crop.AsSpan(y * cw, cw));
        }

        ApngWriter.WriteStill(path, cw, ch, crop);
        Console.WriteLine($"OK {path} ({cw}×{ch})");
    }

    private static OverlayLayout CurrentLayout()
    {
        var monitor = MonitorFromWindow(GetForegroundWindow(), 1);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        GetMonitorInfoW(monitor, &info);
        uint dx, dy;
        var scale = GetDpiForMonitor(monitor, 0, &dx, &dy) == 0 ? dx / 96f : 1f;
        return OverlayMotion.Layout(scale, info.rcMonitor.Bottom, info.rcWork.Left, info.rcWork.Right, info.rcWork.Bottom);
    }

    // ---------- подложка ----------

    private static int _backdropWidth;
    private static int _backdropHeight;
    private static float _capsuleX;
    private static float _capsuleY;
    private static float _unit;

    private static nint CreateBackdrop(int x, int y, int w, int h)
    {
        _backdropWidth = w;
        _backdropHeight = h;
        const string Class = "SaySo.Probe.Backdrop";
        fixed (char* name = Class)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &BackdropProc,
                hInstance = GetModuleHandleW(null),
                lpszClassName = name,
            };
            RegisterClassExW(&wc);
        }

        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        var hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, Class, null, WS_POPUP, x, y, w, h, 0, 0, GetModuleHandleW(null), null);
        ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
        return hwnd;
    }

    [UnmanagedCallersOnly]
    private static nint BackdropProc(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg != 0x000F) // WM_PAINT
        {
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        PAINTSTRUCT ps;
        var dc = BeginPaint(hwnd, &ps);
        var w = _backdropWidth;
        var h = _backdropHeight;
        _backdropPixels ??= PaintBackdrop(w, h);
        var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        fixed (uint* px = _backdropPixels)
        {
            SetDIBitsToDevice(dc, 0, 0, (uint)w, (uint)h, 0, 0, 0, (uint)h, px, &bi, 0);
        }

        EndPaint(hwnd, &ps);
        return 0;
    }

    private static uint[]? _backdropPixels;

    /// <summary>
    /// «Обои»: диагональный градиент и несколько мягких пятен со сглаженными краями. Пятна заходят под капсулу слева
    /// и справа — Acrylic смешивает их цвета. Ничего с настоящего экрана в запись не попадает.
    /// </summary>
    private static uint[] PaintBackdrop(int w, int h)
    {
        var px = new uint[w * h];
        (float X, float Y, float R, int Rgb)[] blobs =
        [
            (-1.7f, 0.35f, 1.35f, 0x2EC4B6), (2.0f, -0.25f, 1.25f, 0xFFB703), (4.7f, 1.25f, 1.5f, 0xFF6B9A),
            (-5.0f, -0.9f, 1.1f, 0xA8DADC), (-3.6f, 1.6f, 0.9f, 0x5E60CE),
        ];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var t = Math.Clamp((x / (float)w) * 0.8f + (y / (float)h) * 0.2f, 0f, 1f);
                var (r, g, b) = Gradient(t);
                foreach (var blob in blobs)
                {
                    var dx = x + 0.5f - (_capsuleX + blob.X * _unit);
                    var dy = y + 0.5f - (_capsuleY + blob.Y * _unit);
                    var cover = Math.Clamp(blob.R * _unit - MathF.Sqrt(dx * dx + dy * dy) + 0.5f, 0f, 1f) * 0.92f;
                    r += (((blob.Rgb >> 16) & 0xFF) - r) * cover;
                    g += (((blob.Rgb >> 8) & 0xFF) - g) * cover;
                    b += ((blob.Rgb & 0xFF) - b) * cover;
                }

                px[y * w + x] = 0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
            }
        }

        return px;
    }

    private static (float R, float G, float B) Gradient(float t)
    {
        // #2B4C7E → #6A4C93 → #E07A5F
        static float L(int a, int b, float k) => a + (b - a) * k;
        return t < 0.5f
            ? (L(0x2B, 0x6A, t * 2), L(0x4C, 0x4C, t * 2), L(0x7E, 0x93, t * 2))
            : (L(0x6A, 0xE0, (t - 0.5f) * 2), L(0x4C, 0x7A, (t - 0.5f) * 2), L(0x93, 0x5F, (t - 0.5f) * 2));
    }

    // ---------- снимок экрана и цикл сообщений ----------

    private static uint[] Grab(RECT r)
    {
        var w = r.Right - r.Left;
        var h = r.Bottom - r.Top;
        var screen = GetDC(0);
        var mem = CreateCompatibleDC(screen);
        var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        void* bits;
        var dib = CreateDIBSection(mem, &bi, 0, &bits, 0, 0);
        var old = SelectObject(mem, dib);
        BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020); // SRCCOPY: DWM и так отдаёт составленный кадр со всеми окнами
        GdiFlush();
        var px = new uint[w * h];
        new Span<uint>(bits, w * h).CopyTo(px);
        for (var i = 0; i < px.Length; i++)
        {
            px[i] |= 0xFF000000; // экран непрозрачен
        }

        SelectObject(mem, old);
        DeleteObject(dib);
        DeleteDC(mem);
        ReleaseDC(0, screen);
        return px;
    }

    private static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        MSG msg;
        do
        {
            while (PeekMessageW(&msg, 0, 0, 0, 1))
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }

            Thread.Sleep(1);
        }
        while (Environment.TickCount64 < until);
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x, y;
        public uint priv;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<nint, uint, nuint, nint, nint> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public nint hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        public fixed byte rgbReserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [LibraryImport("user32.dll")] private static partial nint GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial nint MonitorFromWindow(nint hwnd, uint flags);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetMonitorInfoW(nint monitor, MONITORINFO* info);
    [LibraryImport("shcore.dll")] private static partial int GetDpiForMonitor(nint monitor, int type, uint* x, uint* y);
    [LibraryImport("kernel32.dll")] private static partial nint GetModuleHandleW(char* name);
    [LibraryImport("user32.dll")] private static partial ushort RegisterClassExW(WNDCLASSEXW* wc);
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, void* param);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool ShowWindow(nint hwnd, int cmd);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DestroyWindow(nint hwnd);
    [LibraryImport("user32.dll")] private static partial nint DefWindowProcW(nint hwnd, uint msg, nuint wParam, nint lParam);
    [LibraryImport("user32.dll")] private static partial nint BeginPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool EndPaint(nint hwnd, PAINTSTRUCT* ps);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);
    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool TranslateMessage(MSG* msg);
    [LibraryImport("user32.dll")] private static partial nint DispatchMessageW(MSG* msg);
    [LibraryImport("user32.dll")] private static partial nint GetDC(nint hwnd);
    [LibraryImport("user32.dll")] private static partial int ReleaseDC(nint hwnd, nint dc);
    [LibraryImport("gdi32.dll")]
    private static partial int SetDIBitsToDevice(nint dc, int x, int y, uint w, uint h, int sx, int sy, uint start, uint lines, void* bits, BITMAPINFOHEADER* bi, uint usage);
    [LibraryImport("winmm.dll")] private static partial uint timeBeginPeriod(uint ms);
    [LibraryImport("winmm.dll")] private static partial uint timeEndPeriod(uint ms);
    [LibraryImport("gdi32.dll")] private static partial nint CreateCompatibleDC(nint dc);
    [LibraryImport("gdi32.dll")] private static partial nint CreateDIBSection(nint dc, BITMAPINFOHEADER* bi, uint usage, void** bits, nint section, uint offset);
    [LibraryImport("gdi32.dll")] private static partial nint SelectObject(nint dc, nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteObject(nint obj);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool DeleteDC(nint dc);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool BitBlt(nint dst, int x, int y, int w, int h, nint src, int sx, int sy, uint rop);
    [LibraryImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GdiFlush();
}
