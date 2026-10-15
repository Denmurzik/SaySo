using System.Runtime.InteropServices;

namespace SaySo.Core.Tests.Input;

/// <summary>Вспомогательные Win32-окна для интеграционных тестов вставки.</summary>
internal static unsafe partial class TestWindows
{
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint GW_HWNDNEXT = 2;
    private const int GWL_EXSTYLE = -20;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int x;
        public int y;
        public uint priv;
    }

    public static nint CreateEdit() =>
        CreateWindowExW(0, "Edit", "", WS_OVERLAPPEDWINDOW | WS_VISIBLE, 200, 200, 500, 120, 0, 0, GetModuleHandleW(null), null);

    /// <summary>Окно «всегда поверх», которое не берёт фокус: имитирует чужое topmost-окно, созданное позже плашки.</summary>
    public static nint CreateTopmostCover(int x, int y, int width, int height) =>
        CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, "Static", "", WS_POPUP | WS_VISIBLE,
            x, y, width, height, 0, 0, GetModuleHandleW(null), null);

    public static void Destroy(nint hwnd) => DestroyWindow(hwnd);

    public static bool IsVisible(nint hwnd) => IsWindowVisible(hwnd);

    public static bool IsTopmost(nint hwnd) => (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    public static long ExStyle(nint hwnd) => GetWindowLongPtrW(hwnd, GWL_EXSTYLE);

    /// <summary>true — <paramref name="upper"/> выше <paramref name="lower"/> в порядке окон верхнего уровня.</summary>
    public static bool IsAbove(nint upper, nint lower)
    {
        for (var hwnd = GetWindow(upper, GW_HWNDNEXT); hwnd != 0; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
        {
            if (hwnd == lower)
            {
                return true;
            }
        }

        return false;
    }

    public static string GetText(nint hwnd)
    {
        var length = GetWindowTextLengthW(hwnd);
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var n = GetWindowTextW(hwnd, p, buffer.Length);
            return new string(p, 0, n);
        }
    }

    public static void Pump(int milliseconds)
    {
        var until = Environment.TickCount64 + milliseconds;
        MSG msg;
        while (Environment.TickCount64 < until)
        {
            while (PeekMessageW(&msg, 0, 0, 0, 1) != 0)
            {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }

            Thread.Sleep(5);
        }
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint ex, string cls, string? title, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, void* param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextW(nint hwnd, char* text, int max);

    [LibraryImport("user32.dll")]
    private static partial int PeekMessageW(MSG* msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    private static partial int TranslateMessage(MSG* msg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(MSG* msg);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
