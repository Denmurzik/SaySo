using SaySo.Core.Interop;

namespace SaySo.Core.Input;

/// <summary>
/// Вставка текста в окно переднего плана через буфер обмена и Ctrl+V с восстановлением буфера.
/// Потоковый контракт: каждая операция с буфером (Open/Close) синхронна внутри одного вызова, но
/// <see cref="InsertAsync"/> делает несколько таких вызовов с ожиданиями между ними, поэтому экземпляр используют
/// на потоке, создавшем окно-владелец (<see cref="ClipboardOwner"/>), и с SynchronizationContext, который возвращает
/// продолжения на этот же поток (главный поток демона SaySo). Окно-владелец уничтожается на создавшем его потоке.
/// </summary>
public sealed class TextInserter
{
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan JoinWindow = SpaceJoiner.JoinWindow;

    private readonly nint _clipboardOwner;
    private readonly TimeProvider _time;

    public TextInserter(nint clipboardOwner, TimeProvider time)
    {
        if (clipboardOwner == 0)
        {
            throw new ArgumentException("Нужно окно-владелец буфера обмена (с NULL-владельцем SetClipboardData не работает).", nameof(clipboardOwner));
        }

        _clipboardOwner = clipboardOwner;
        _time = time;
    }

    public LastInsertion? Last { get; private set; }

    /// <summary>Идентификатор «своего» процесса; тесты подменяют его, чтобы вставлять в окно из того же процесса.</summary>
    internal uint OwnProcessId { get; init; } = (uint)Environment.ProcessId;

    public async Task<InsertOutcome> InsertAsync(string text, IReadOnlyCollection<int> keysToRelease, nint expectedForeground = 0)
    {
        await KeyboardInjector.WaitForKeysReleasedAsync(keysToRelease, TimeSpan.FromSeconds(1)).ConfigureAwait(true);

        var target = Native.GetForegroundWindow();
        if (target == 0 || (expectedForeground != 0 && target != expectedForeground) || BelongsToProcess(target, OwnProcessId))
        {
            // Окно нашего же процесса (например, меню трея, пока сессия завершается): Ctrl+V ушёл бы в никуда, а вставка
            // считалась бы успешной.
            return InsertOutcome.NoTarget;
        }

        if (ProcessIntegrity.IsHigherThanCurrent(target))
        {
            // Нажатия в админ-окно заблокирует UIPI — оставляем текст в буфере для ручной вставки.
            return ClipboardText.SetText(_clipboardOwner, text, excludeFromHistory: false)
                ? InsertOutcome.BlockedByElevation
                : InsertOutcome.ClipboardUnavailable;
        }

        var prepared = SpaceJoiner.Prepare(text, Last, target, _time.GetUtcNow());
        if (!ClipboardText.TryGetText(_clipboardOwner, out var previous)
            || !ClipboardText.SetText(_clipboardOwner, prepared, excludeFromHistory: true))
        {
            return InsertOutcome.ClipboardUnavailable;
        }

        var ours = ClipboardText.SequenceNumber;

        // Фокус мог уйти, пока писали в буфер: нажатия в другое окно не отправляем.
        if (Native.GetForegroundWindow() != target)
        {
            TryRestore(previous, ours);
            return InsertOutcome.NoTarget;
        }

        if (!KeyboardInjector.SendCtrlV())
        {
            // Система приняла не все нажатия (защищённый рабочий стол, BlockInput): текст остаётся в буфере для ручной вставки.
            ClipboardText.SetText(_clipboardOwner, text, excludeFromHistory: false);
            return InsertOutcome.InputBlocked;
        }

        Last = new LastInsertion(target, _time.GetUtcNow(), prepared);

        await Task.Delay(RestoreDelay).ConfigureAwait(true);
        if (!TryRestore(previous, ours))
        {
            // Буфер был занят — одна повторная попытка, если он по-прежнему наш.
            await Task.Delay(RestoreDelay).ConfigureAwait(true);
            TryRestore(previous, ours);
        }

        return InsertOutcome.Inserted;
    }

    internal static unsafe bool BelongsToProcess(nint hwnd, uint processId)
    {
        uint pid;
        Native.GetWindowThreadProcessId(hwnd, &pid);
        return pid == processId;
    }

    /// <returns>false — прежний текст нужно вернуть, но запись не удалась.</returns>
    private bool TryRestore(string? previous, uint ours) =>
        previous is null
        || ClipboardText.SequenceNumber != ours
        || ClipboardText.SetText(_clipboardOwner, previous, excludeFromHistory: true);
}
