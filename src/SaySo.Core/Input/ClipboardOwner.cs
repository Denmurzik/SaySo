using SaySo.Core.Interop;

namespace SaySo.Core.Input;

/// <summary>Скрытое message-only окно предопределённого класса Static — владелец буфера обмена.</summary>
public static unsafe class ClipboardOwner
{
    public static nint Create()
    {
        var hwnd = Native.CreateWindowExW(0, "Static", null, 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, Native.GetModuleHandleW(null), null);
        return hwnd != 0 ? hwnd : throw new InvalidOperationException("Не удалось создать окно-владельца буфера обмена.");
    }

    public static void Destroy(nint hwnd) => Native.DestroyWindow(hwnd);
}
