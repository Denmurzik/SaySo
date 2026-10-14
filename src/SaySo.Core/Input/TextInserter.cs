using SaySo.Core.Interop;

namespace SaySo.Core.Input;

public enum InsertOutcome
{
    Inserted,
    BlockedByElevation,
    ClipboardUnavailable,
    NoTarget,
}

/// <summary>Вставка текста в окно переднего плана через буфер обмена и Ctrl+V с восстановлением буфера.</summary>
public sealed class TextInserter(nint clipboardOwner, TimeProvider time)
{
    public static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan JoinWindow = TimeSpan.FromSeconds(60);

    public LastInsertion? Last { get; private set; }

    public async Task<InsertOutcome> InsertAsync(string text, IReadOnlyCollection<int> keysToRelease, nint expectedForeground = 0)
    {
        await KeyboardInjector.WaitForKeysReleasedAsync(keysToRelease, TimeSpan.FromSeconds(1)).ConfigureAwait(true);

        var target = Native.GetForegroundWindow();
        if (target == 0 || (expectedForeground != 0 && target != expectedForeground))
        {
            return InsertOutcome.NoTarget;
        }

        if (ProcessIntegrity.IsHigherThanCurrent(target))
        {
            // Нажатия в админ-окно заблокирует UIPI — оставляем текст в буфере для ручной вставки.
            ClipboardText.SetText(clipboardOwner, text, excludeFromHistory: false);
            return InsertOutcome.BlockedByElevation;
        }

        var prepared = SpaceJoiner.Prepare(text, Last, target, time.GetUtcNow());
        if (!ClipboardText.TryGetText(clipboardOwner, out var previous)
            || !ClipboardText.SetText(clipboardOwner, prepared, excludeFromHistory: true))
        {
            return InsertOutcome.ClipboardUnavailable;
        }

        var ours = ClipboardText.SequenceNumber;
        KeyboardInjector.SendCtrlV();
        Last = new LastInsertion(target, time.GetUtcNow(), prepared);

        await Task.Delay(RestoreDelay).ConfigureAwait(true);
        if (previous is not null && ClipboardText.SequenceNumber == ours)
        {
            ClipboardText.SetText(clipboardOwner, previous, excludeFromHistory: true);
        }

        return InsertOutcome.Inserted;
    }
}
