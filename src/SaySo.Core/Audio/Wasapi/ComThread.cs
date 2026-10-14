using Windows.Win32;
using Windows.Win32.System.Com;

namespace SaySo.Core.Audio.Wasapi;

/// <summary>
/// Выполняет действие на коротком MTA-потоке с инициализированным COM. Нужен, потому что вызывающий
/// поток может быть STA (окно настроек WinUI) — CoInitializeEx(MTA) там вернул бы RPC_E_CHANGED_MODE.
/// </summary>
internal static unsafe class ComThread
{
    public static T Run<T>(Func<T> action)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var hr = Win32.CoInitializeEx(null, COINIT.COINIT_MULTITHREADED);
            try
            {
                result = action();
            }
            catch (Exception e)
            {
                error = e;
            }
            finally
            {
                if (hr.Succeeded)
                {
                    Win32.CoUninitialize();
                }
            }
        })
        {
            IsBackground = true,
            Name = "SaySo.Com",
        };
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }

        return result;
    }
}
