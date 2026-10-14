using System.Runtime.InteropServices;

namespace SaySo.Core.Interop;

internal static partial class Native
{
    [LibraryImport("user32.dll")]
    public static partial uint GetDpiForWindow(nint hwnd);
}
