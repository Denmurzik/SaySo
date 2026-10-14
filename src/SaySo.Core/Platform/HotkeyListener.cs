using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>Автомат горячей клавиши на потоке хука; действия уходят главному окну через PostMessage.</summary>
internal sealed class HotkeyListener : IDisposable
{
    private readonly nint _notifyHwnd;
    private readonly uint _notifyMessage;
    private readonly LowLevelKeyboardHook _hook;
    private HotkeyStateMachine _machine; // только поток хука
    private bool _suspended;             // только поток хука

    public HotkeyListener(nint notifyHwnd, uint notifyMessage, IReadOnlyList<int> keys, int tapThresholdMs)
    {
        _notifyHwnd = notifyHwnd;
        _notifyMessage = notifyMessage;
        _machine = Create(keys, tapThresholdMs);
        _hook = new LowLevelKeyboardHook(OnKey);
    }

    public void Configure(IReadOnlyList<int> keys, int tapThresholdMs)
    {
        var copy = keys.ToArray();
        _hook.Post(() => _machine = Create(copy, tapThresholdMs));
    }

    public void Reset() => _hook.Post(() => _machine.Reset());

    public void SetSuspended(bool suspended) => _hook.Post(() =>
    {
        _suspended = suspended;
        _machine.Reset();
    });

    public void Dispose() => _hook.Dispose();

    private static HotkeyStateMachine Create(IReadOnlyList<int> keys, int threshold) =>
        new(keys, threshold, KeyboardInjector.IsKeyDown);

    private bool OnKey(KeyEvent e)
    {
        if (_suspended)
        {
            return false;
        }

        var decision = _machine.OnKey(e);
        if (decision.Action != HotkeyAction.None)
        {
            if (decision.Action == HotkeyAction.StartRecording && _machine.NeedsMenuMask)
            {
                KeyboardInjector.SendMenuMask();
            }

            Native.PostMessageW(_notifyHwnd, _notifyMessage, (nuint)decision.Action, 0);
        }

        return decision.Swallow;
    }
}
