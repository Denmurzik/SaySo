using System.Runtime.InteropServices;

namespace SaySo.Core.Interop;

/// <summary>
/// Интероп плашки v2 (Windows.UI.Composition поверх окна Win32). Функции с WinRT-типами в сигнатуре CsWin32 оборачивает
/// в WinRTCustomMarshaler (рефлексия, не AOT), поэтому очередь, ICompositorDesktopInterop и глифы GDI объявлены здесь вручную;
/// IID и порядок методов — из windows.ui.composition.interop.h (SDK 10.0.26100).
/// </summary>
internal static unsafe partial class Native
{
    public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
    public const uint LWA_ALPHA = 0x2;
    public const uint DWMWA_USE_HOSTBACKDROPBRUSH = 17;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [StructLayout(LayoutKind.Sequential)]
    public struct DispatcherQueueOptions
    {
        public uint dwSize;
        public int threadType;    // 2 = DQTYPE_THREAD_CURRENT
        public int apartmentType; // 2 = DQTAT_COM_STA
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag; // 1 — включена экономия заряда
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [LibraryImport("CoreMessaging.dll")]
    public static partial int CreateDispatcherQueueController(DispatcherQueueOptions options, nint* controller);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, void* value, uint size);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint action, uint param, void* value, uint winIni);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(SYSTEM_POWER_STATUS* status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool K32GetProcessMemoryInfo(nint process, PROCESS_MEMORY_COUNTERS_EX* counters, uint size);

    [LibraryImport("user32.dll")]
    public static partial nint WindowFromPoint(POINT point);

    // ---------- COM без проекций ----------

    /// <summary>ICompositorDesktopInterop: 3 CreateDesktopWindowTarget(HWND, BOOL, IDesktopWindowTarget**), 4 EnsureOnThread.</summary>
    public static readonly Guid IID_ICompositorDesktopInterop = new("29E691FA-4567-4DCA-B319-D0F207EB6807");

    public static int QueryInterface(nint unknown, in Guid iid, out nint result)
    {
        fixed (Guid* p = &iid)
        fixed (nint* r = &result)
        {
            return ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(void***)unknown)[0])(unknown, p, r);
        }
    }

    public static uint AddRef(nint unknown) => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)unknown)[1])(unknown);

    public static void Release(nint unknown)
    {
        if (unknown != 0)
        {
            ((delegate* unmanaged[Stdcall]<nint, uint>)(*(void***)unknown)[2])(unknown);
        }
    }

    public static int CreateDesktopWindowTarget(nint desktopInterop, nint hwnd, bool topmost, out nint target)
    {
        fixed (nint* t = &target)
        {
            return ((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)(*(void***)desktopInterop)[3])(desktopInterop, hwnd, topmost ? 1 : 0, t);
        }
    }

    // ---------- контуры глифов GDI ----------

    public const uint GGO_BEZIER = 3;
    public const uint GGO_GLYPH_INDEX = 0x80;
    public const uint GGO_UNHINTED = 0x100;
    public const uint GDI_ERROR = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct MAT2
    {
        public int eM11;
        public int eM12;
        public int eM21;
        public int eM22;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GLYPHMETRICS
    {
        public uint gmBlackBoxX;
        public uint gmBlackBoxY;
        public int gmptGlyphOriginX;
        public int gmptGlyphOriginY;
        public short gmCellIncX;
        public short gmCellIncY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TEXTMETRICW
    {
        public int tmHeight;
        public int tmAscent;
        public int tmDescent;
        public int tmInternalLeading;
        public int tmExternalLeading;
        public int tmAveCharWidth;
        public int tmMaxCharWidth;
        public int tmWeight;
        public int tmOverhang;
        public int tmDigitizedAspectX;
        public int tmDigitizedAspectY;
        public char tmFirstChar;
        public char tmLastChar;
        public char tmDefaultChar;
        public char tmBreakChar;
        public byte tmItalic;
        public byte tmUnderlined;
        public byte tmStruckOut;
        public byte tmPitchAndFamily;
        public byte tmCharSet;
    }

    [LibraryImport("gdi32.dll")]
    public static partial uint GetGlyphOutlineW(nint hdc, uint ch, uint format, GLYPHMETRICS* metrics, uint size, void* buffer, MAT2* matrix);

    [LibraryImport("gdi32.dll")]
    public static partial uint GetGlyphIndicesW(nint hdc, char* text, int count, ushort* indices, uint flags);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTextMetricsW(nint hdc, TEXTMETRICW* metrics);
}
