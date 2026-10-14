using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

internal static unsafe class MessageLoop
{
    /// <summary>Классический цикл GetMessage до WM_QUIT; возвращает код из PostQuitMessage.</summary>
    public static int Run()
    {
        Native.MSG msg;
        int result;
        while ((result = Native.GetMessageW(&msg, 0, 0, 0)) != 0)
        {
            if (result == -1)
            {
                return -1;
            }

            Native.TranslateMessage(&msg);
            Native.DispatchMessageW(&msg);
        }

        return (int)msg.wParam;
    }
}
