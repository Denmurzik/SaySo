using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Settings;

/// <summary>
/// Запись новой комбинации в окне настроек: локальный монитор событий клавиатуры (addLocalMonitorForEventsMatchingMask)
/// видит нажатия, отпускания и модификаторы, пока окно в фокусе, — разрешения macOS для этого не нужны. Все клавиши во
/// время записи поглощаются (не доходят до полей окна), а демон на это время приостанавливает горячую клавишу (IPC).
/// Запись одна на процесс.
/// </summary>
internal static class HotkeyCapture
{
    private const ulong KeyEventsMask = (1UL << 10) | (1UL << 11) | (1UL << 12); // keyDown, keyUp, flagsChanged
    private const long KeyDown = 10;
    private const long KeyUp = 11;
    private const long FlagsChanged = 12;

    private static HotkeyRecorder? _recorder;
    private static TaskCompletionSource<IReadOnlyList<int>?>? _active;
    private static nint _block;

    public static bool IsActive => _active is not null;

    /// <summary>Прервать идущую запись (уход со страницы, закрытие окна); результат записи — null.</summary>
    public static void CancelActive() => _active?.TrySetResult(null);

    /// <returns>Комбинация; null — Esc, таймаут или <see cref="CancelActive"/>.</returns>
    public static async Task<IReadOnlyList<int>?> CaptureAsync(TimeSpan timeout)
    {
        if (_active is not null)
        {
            throw new InvalidOperationException("Запись горячей клавиши уже идёт.");
        }

        var done = new TaskCompletionSource<IReadOnlyList<int>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _active = done;
        _recorder = new HotkeyRecorder();
        _block = _block != 0 ? _block : CreateBlock();
        var monitor = ObjC.Retain(ObjC.Send(ObjC.Class("NSEvent"), ObjC.Sel("addLocalMonitorForEventsMatchingMask:handler:"),
            (nint)KeyEventsMask, _block));
        try
        {
            var winner = await Task.WhenAny(done.Task, Task.Delay(timeout));
            return winner == done.Task ? await done.Task : null;
        }
        finally
        {
            ObjC.SendVoid(ObjC.Class("NSEvent"), ObjC.Sel("removeMonitor:"), monitor);
            ObjC.Release(monitor);
            _recorder = null;
            _active = null;
        }
    }

    private static unsafe nint CreateBlock() => Blocks.Global((nint)(delegate* unmanaged<nint, nint, nint>)&OnEvent);

    [UnmanagedCallersOnly]
    private static nint OnEvent(nint block, nint ev)
    {
        try
        {
            if (_recorder is not { } recorder || _active is not { } done)
            {
                return ev;
            }

            var type = ObjC.SendLong(ev, ObjC.Sel("type"));
            var code = (int)(ushort)ObjC.SendLong(ev, ObjC.Sel("keyCode"));
            var vk = MacKeyCodes.ToVirtualKey(code);
            bool? down = type switch
            {
                KeyDown => true,
                KeyUp => false,
                FlagsChanged when MacKeyCodes.IsModifierKey(code) =>
                    MacKeyCodes.IsModifierDown(code, (ulong)ObjC.SendLong(ev, ObjC.Sel("modifierFlags"))),
                _ => null,
            };

            // Клавиши без пары VK (Caps Lock и прочие) в комбинацию не попадают: их не сохранить в настройках.
            if (down is { } isDown && vk < MacKeyCodes.UnmappedBase
                && recorder.OnKey(new KeyEvent(vk, isDown, false, Environment.TickCount64)))
            {
                done.TrySetResult(recorder.IsCancelled ? null : recorder.Result);
            }
        }
        catch (Exception e)
        {
            Log.Error("Запись горячей клавиши", e);
        }

        return 0; // поглощаем: во время записи клавиши не должны печатать в поля окна
    }
}
