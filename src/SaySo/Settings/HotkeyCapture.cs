using SaySo.Core.Hotkeys;
using SaySo.Core.Platform;

namespace SaySo.Settings;

/// <summary>
/// Запись новой комбинации через собственный LL-хук окна настроек. Все клавиши во время записи поглощаются
/// (не доходят ни до приложений, ни до хука демона — он установлен раньше и вызывается позже).
/// </summary>
internal static class HotkeyCapture
{
    public static async Task<IReadOnlyList<int>?> CaptureAsync(TimeSpan timeout)
    {
        var recorder = new HotkeyRecorder();
        var done = new TaskCompletionSource<IReadOnlyList<int>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hook = new LowLevelKeyboardHook(e =>
        {
            if (recorder.OnKey(e))
            {
                done.TrySetResult(recorder.IsCancelled ? null : recorder.Result);
            }

            return true;
        });
        var winner = await Task.WhenAny(done.Task, Task.Delay(timeout));
        return winner == done.Task ? await done.Task : null;
    }
}
