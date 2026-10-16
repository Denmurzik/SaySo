using SaySo.Core.Diagnostics;
using SaySo.Core.Input;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>
/// Вставка текста в активное приложение macOS: буфер обмена + Cmd+V, затем прежнее содержимое буфера возвращается (всё,
/// что в нём было: текст, картинки, файлы), если после нас буфер никто не менял. «Окно» вставки — PID приложения на
/// переднем плане (для пробела между подряд идущими диктовками и для «Вставить последнее»). Вызывается на главном
/// потоке с контекстом синхронизации, который возвращает продолжения на него.
/// </summary>
internal sealed class MacTextInserter(TimeProvider time)
{
    /// <summary>Сколько ждать, пока приложение прочитает буфер после Cmd+V: Electron и Java читают его не сразу.</summary>
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(400);

    public LastInsertion? Last { get; private set; }

    public async Task<InsertOutcome> InsertAsync(string text, IReadOnlyCollection<int> keysToRelease, int expectedPid = 0)
    {
        await Keyboard.WaitForKeysReleasedAsync(keysToRelease, TimeSpan.FromSeconds(1)).ConfigureAwait(true);

        var target = NS.FrontmostPid();
        if (target == 0 || (expectedPid != 0 && target != expectedPid) || target == Environment.ProcessId)
        {
            // Наше же приложение (окно настроек, меню строки меню): Cmd+V ушёл бы в никуда, а вставка считалась бы успешной.
            return InsertOutcome.NoTarget;
        }

        if (!Keyboard.IsAccessibilityTrusted)
        {
            // Без «Универсального доступа» синтетическое Cmd+V молча теряется: оставляем текст в буфере для ручной вставки.
            return Pasteboard.SetText(text, transient: false) ? InsertOutcome.AccessibilityDenied : InsertOutcome.ClipboardUnavailable;
        }

        var prepared = SpaceJoiner.Prepare(text, Last, target, time.GetUtcNow());
        using var previous = Pasteboard.Save();
        if (!Pasteboard.SetText(prepared, transient: true))
        {
            return InsertOutcome.ClipboardUnavailable;
        }

        var ours = Pasteboard.ChangeCount;

        // Фокус мог уйти, пока писали в буфер: нажатие в другое приложение не отправляем.
        if (NS.FrontmostPid() != target)
        {
            TryRestore(previous, ours);
            return InsertOutcome.NoTarget;
        }

        if (!Keyboard.SendCmdV())
        {
            Pasteboard.SetText(text, transient: false);
            return InsertOutcome.InputBlocked;
        }

        Last = new LastInsertion(target, time.GetUtcNow(), prepared);

        await Task.Delay(RestoreDelay).ConfigureAwait(true);
        if (!TryRestore(previous, ours))
        {
            await Task.Delay(RestoreDelay).ConfigureAwait(true);
            TryRestore(previous, ours);
        }

        return InsertOutcome.Inserted;
    }

    /// <returns>false — вернуть нужно, но запись не удалась.</returns>
    private static bool TryRestore(Pasteboard.Snapshot previous, long ours)
    {
        if (Pasteboard.ChangeCount != ours)
        {
            return true; // буфер уже изменил кто-то другой (пользователь скопировал новое): его не трогаем
        }

        if (previous.IsEmpty)
        {
            return true; // буфер был пуст: диктовка остаётся в нём, но помечена как служебная
        }

        if (previous.Restore())
        {
            return true;
        }

        Log.Info("Буфер обмена не восстановлен с первой попытки");
        return false;
    }
}
