using SaySo.Core.Hotkeys;
using SaySo.Core.Platform;

namespace SaySo.Settings;

/// <summary>
/// Запись новой комбинации через собственный LL-хук окна настроек. Все клавиши во время записи поглощаются
/// (не доходят ни до приложений, ни до хука демона — он установлен раньше и вызывается позже).
/// В процессе допустим только один LL-хук, поэтому запись одна на процесс: <see cref="IsActive"/> защищает от второй.
/// </summary>
internal static class HotkeyCapture
{
    private static TaskCompletionSource<IReadOnlyList<int>?>? s_active;

    /// <summary>Запись идёт (хук установлен или ещё снимается).</summary>
    public static bool IsActive => Volatile.Read(ref s_active) is not null;

    /// <summary>Прервать идущую запись (уход со страницы, закрытие окна); результат записи — null.</summary>
    public static void CancelActive() => Volatile.Read(ref s_active)?.TrySetResult(null);

    /// <returns>Комбинация; null — Esc, таймаут или <see cref="CancelActive"/>.</returns>
    /// <exception cref="InvalidOperationException">Запись уже идёт.</exception>
    public static async Task<IReadOnlyList<int>?> CaptureAsync(TimeSpan timeout)
    {
        var recorder = new HotkeyRecorder();
        var done = new TaskCompletionSource<IReadOnlyList<int>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Interlocked.CompareExchange(ref s_active, done, null) is not null)
        {
            throw new InvalidOperationException("Запись горячей клавиши уже идёт.");
        }

        try
        {
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
        finally
        {
            // Флаг снимается после Dispose хука (using выше завершается раньше finally): новый хук можно ставить сразу.
            Volatile.Write(ref s_active, null);
        }
    }
}
