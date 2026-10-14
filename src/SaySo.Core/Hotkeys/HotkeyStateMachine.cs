namespace SaySo.Core.Hotkeys;

/// <summary>
/// Конечный автомат горячей клавиши: удержание — push-to-talk, короткое нажатие — «руки свободны»,
/// Esc или посторонняя клавиша при удержании — отмена. Вызывается из колбэка LL-хука — должен быть быстрым.
/// </summary>
public sealed class HotkeyStateMachine
{
    private readonly HashSet<int> _hotkey;
    private readonly HashSet<int> _down = [];
    private readonly Func<int, bool>? _isPhysicallyDown;
    private State _state = State.Idle;
    private long _pressedAt;

    public HotkeyStateMachine(IEnumerable<int> hotkeyKeys, int tapThresholdMs, Func<int, bool>? isPhysicallyDown = null)
    {
        _hotkey = [.. hotkeyKeys];
        if (_hotkey.Count == 0)
        {
            throw new ArgumentException("Комбинация не может быть пустой.", nameof(hotkeyKeys));
        }

        TapThresholdMs = tapThresholdMs;
        _isPhysicallyDown = isPhysicallyDown;
        NeedsMenuMask = _hotkey.Any(vk => vk is VirtualKeys.LWin or VirtualKeys.RWin
            or VirtualKeys.LMenu or VirtualKeys.RMenu or 0x12);
    }

    private enum State
    {
        Idle,
        Held,
        HandsFree,
    }

    public int TapThresholdMs { get; }

    public bool IsRecording => _state != State.Idle;

    public bool IsHandsFree => _state == State.HandsFree;

    /// <summary>Комбинация содержит Win или Alt — при старте записи нужно инжектировать маскирующую клавишу.</summary>
    public bool NeedsMenuMask { get; }

    public HotkeyDecision OnKey(KeyEvent e)
    {
        if (e.IsInjected)
        {
            return HotkeyDecision.Pass;
        }

        if (!e.IsDown)
        {
            _down.Remove(e.VirtualKey);
            return OnKeyUp(e);
        }

        if (!_down.Add(e.VirtualKey))
        {
            return HotkeyDecision.Pass; // автоповтор
        }

        if (_down.Count > 1)
        {
            PruneStale(except: e.VirtualKey);
        }

        return OnKeyDown(e);
    }

    /// <summary>Вернуться в Idle (сессия завершилась извне) и выбросить «залипшие» клавиши.</summary>
    public void Reset()
    {
        _state = State.Idle;
        PruneStale(except: null);
    }

    private HotkeyDecision OnKeyDown(KeyEvent e)
    {
        switch (_state)
        {
            case State.Idle:
                if (ComboExactlyDown())
                {
                    _state = State.Held;
                    _pressedAt = e.TimestampMs;
                    return new HotkeyDecision(HotkeyAction.StartRecording, false);
                }

                return HotkeyDecision.Pass;

            case State.Held:
                // Посторонняя клавиша при удержании — это сочетание клавиш пользователя, а не диктовка.
                _state = State.Idle;
                return new HotkeyDecision(HotkeyAction.Cancel, e.VirtualKey == VirtualKeys.Escape);

            default: // HandsFree
                if (e.VirtualKey == VirtualKeys.Escape)
                {
                    _state = State.Idle;
                    return new HotkeyDecision(HotkeyAction.Cancel, true);
                }

                if (ComboExactlyDown())
                {
                    _state = State.Idle;
                    return new HotkeyDecision(HotkeyAction.StopRecording, false);
                }

                return HotkeyDecision.Pass;
        }
    }

    private HotkeyDecision OnKeyUp(KeyEvent e)
    {
        if (_state != State.Held || !_hotkey.Contains(e.VirtualKey))
        {
            return HotkeyDecision.Pass;
        }

        var heldMs = unchecked((uint)e.TimestampMs - (uint)_pressedAt);
        if (heldMs < TapThresholdMs)
        {
            _state = State.HandsFree;
            return HotkeyDecision.Pass;
        }

        _state = State.Idle;
        return new HotkeyDecision(HotkeyAction.StopRecording, false);
    }

    private bool ComboExactlyDown() => _down.Count == _hotkey.Count && _down.SetEquals(_hotkey);

    private void PruneStale(int? except)
    {
        if (_isPhysicallyDown is null)
        {
            return;
        }

        _down.RemoveWhere(vk => vk != except && !_isPhysicallyDown(vk));
    }
}
