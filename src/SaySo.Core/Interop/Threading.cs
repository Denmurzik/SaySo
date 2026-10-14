using System.Runtime.InteropServices;

namespace SaySo.Core.Interop;

/// <summary>Объявления Win32 (LibraryImport, без runtime-маршалинга). Класс дополняется в Interop/*.cs.</summary>
internal static unsafe partial class Native
{
    public const uint WAIT_OBJECT_0 = 0;
    public const uint WAIT_TIMEOUT = 0x102;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateEventW(void* securityAttributes, int manualReset, int initialState, char* name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}
