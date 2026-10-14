using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;

namespace SaySo.Settings;

/// <summary>
/// Минимальный размер окна через подкласс окна (WM_GETMINMAXINFO). Размер задаётся в DIP и пересчитывается по
/// DPI монитора в момент сообщения, поэтому остаётся верным при переносе окна между мониторами.
/// </summary>
internal static unsafe partial class WindowMinSize
{
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const nuint SubclassId = 0x5359; // "SY"

    private static int s_minWidthDip;
    private static int s_minHeightDip;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public int ReservedX, ReservedY;
        public int MaxSizeX, MaxSizeY;
        public int MaxPositionX, MaxPositionY;
        public int MinTrackX, MinTrackY;
        public int MaxTrackX, MaxTrackY;
    }

    public static void Apply(nint hwnd, int minWidthDip, int minHeightDip)
    {
        s_minWidthDip = minWidthDip;
        s_minHeightDip = minHeightDip;
        if (!SetWindowSubclass(hwnd, &SubclassProc, SubclassId, 0))
        {
            Log.Info("Не удалось задать минимальный размер окна настроек.");
        }
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint refData)
    {
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == WM_GETMINMAXINFO)
        {
            try
            {
                var scale = Native.GetDpiForWindow(hwnd) / 96.0;
                var info = (MinMaxInfo*)lParam;
                info->MinTrackX = (int)(s_minWidthDip * scale);
                info->MinTrackY = (int)(s_minHeightDip * scale);
            }
            catch (Exception)
            {
                // Исключение нельзя пробросить в нативный код; без минимума окно остаётся работоспособным.
            }
        }

        return result;
    }

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> proc, nuint id, nuint refData);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
