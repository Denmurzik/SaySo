using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Daemon;

/// <summary>
/// Скрытое окно верхнего уровня демона: IPC, обратные вызовы трея, хука и таймеров, продолжения async.
/// Никогда не показывается; верхнего уровня — ради TaskbarCreated и меню трея.
/// </summary>
internal sealed class DaemonWindow : IDisposable
{
    public const uint SyncMessage = Native.WM_APP + 1;
    public const uint HotkeyMessage = Native.WM_APP + 2;
    public const uint TrayMessage = Native.WM_APP + 3;

    private const uint WM_POWERBROADCAST = 0x0218;
    private const uint PBT_APMRESUMEAUTOMATIC = 0x0012;

    private readonly MessageWindow _window;
    private readonly Dictionary<uint, string> _ipc = [];
    private readonly Dictionary<nuint, (Action Callback, bool Repeat)> _timers = [];
    private readonly uint _taskbarCreated;
    private nuint _nextTimerId = 100;

    public DaemonWindow()
    {
        _window = new MessageWindow(IpcProtocol.WindowClass, OnMessage, messageOnly: false);
        Sync = new Win32SynchronizationContext(_window.Handle, SyncMessage);
        foreach (var name in IpcProtocol.AllMessages)
        {
            var id = Native.RegisterWindowMessageW(name);
            _ipc[id] = name;
            AllowFromLowerIntegrity(id);
        }

        _taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");
        AllowFromLowerIntegrity(_taskbarCreated);
        // Элевированный демон иначе не получит клики по значку от Проводника и смену темы от системы.
        AllowFromLowerIntegrity(TrayMessage);
        AllowFromLowerIntegrity(Native.WM_SETTINGCHANGE);
        Native.WTSRegisterSessionNotification(_window.Handle, Native.NOTIFY_FOR_THIS_SESSION);
        RegisterKeyboardInput(_window.Handle, remove: false);
    }

    public event Action<HotkeyAction>? HotkeyPressed;

    /// <summary>Событие значка трея (LOWORD(lParam) в режиме NOTIFYICON_VERSION_4).</summary>
    public event Action<uint>? TrayEvent;

    public event Action<string>? IpcMessage;

    public event Action? TaskbarCreated;

    /// <summary>true — сеанс заблокирован, false — разблокирован.</summary>
    public event Action<bool>? SessionLockChanged;

    public event Action? ThemeChanged;

    /// <summary>Выход из сна/гибернации (PBT_APMRESUMEAUTOMATIC): хук мог быть снят системой.</summary>
    public event Action? PowerResumed;

    /// <summary>Нажатие или отпускание клавиши в любом окне (Raw Input); аргумент — время события, как GetMessageTime.</summary>
    public event Action<uint>? KeyboardInput;

    /// <summary>
    /// WM_CLOSE (например, <c>taskkill</c> без /F): штатное завершение, как по IPC-сообщению Quit.
    /// Без обработчика окно демона уничтожилось бы, а процесс с хуком продолжил бы жить без IPC-окна.
    /// </summary>
    public event Action? CloseRequested;

    public nint Handle => _window.Handle;

    public Win32SynchronizationContext Sync { get; }

    public IDisposable SetTimer(TimeSpan due, Action callback, bool repeat = false)
    {
        var id = _nextTimerId++;
        _timers[id] = (callback, repeat);
        nuint result;
        unsafe
        {
            result = Native.SetTimer(_window.Handle, id, (uint)Math.Max(1, due.TotalMilliseconds), null);
        }

        if (result == 0)
        {
            _timers.Remove(id);
            // Молча «потерянный» таймер оставил бы вызывающего без его обратного вызова — сообщаем исключением.
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError(), "SetTimer");
        }

        return new TimerHandle(this, id);
    }

    public void Dispose()
    {
        foreach (var id in _timers.Keys.ToList())
        {
            KillTimer(id);
        }

        Native.WTSUnRegisterSessionNotification(_window.Handle);
        RegisterKeyboardInput(0, remove: true);
        _window.Dispose();
    }

    // Raw Input клавиатуры с RIDEV_INPUTSINK: события приходят и без фокуса. Нужны сторожу хука (HookWatchdog).
    private static unsafe void RegisterKeyboardInput(nint hwnd, bool remove)
    {
        var device = new Native.RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // Generic Desktop
            usUsage = 0x06,     // Keyboard
            dwFlags = remove ? Native.RIDEV_REMOVE : Native.RIDEV_INPUTSINK,
            hwndTarget = hwnd,
        };
        if (!Native.RegisterRawInputDevices(&device, 1, (uint)sizeof(Native.RAWINPUTDEVICE)) && !remove)
        {
            Log.Info($"RegisterRawInputDevices: код {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}; сторож хука выключен");
        }
    }

    private unsafe void AllowFromLowerIntegrity(uint message) =>
        // Если демон запущен от администратора, без этого UIPI отбросит сообщения из окна настроек и Проводника.
        Native.ChangeWindowMessageFilterEx(_window.Handle, message, Native.MSGFLT_ALLOW, null);

    private void KillTimer(nuint id)
    {
        if (_window.Handle != 0)
        {
            Native.KillTimer(_window.Handle, id);
        }

        _timers.Remove(id);
    }

    private nint? OnMessage(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            case SyncMessage:
                Sync.Drain();
                return 0;
            case HotkeyMessage:
                HotkeyPressed?.Invoke((HotkeyAction)wParam);
                return 0;
            case TrayMessage:
                TrayEvent?.Invoke((uint)(lParam & 0xFFFF));
                return 0;
            case Native.WM_CLOSE:
                CloseRequested?.Invoke();
                return 0;
            case Native.WM_INPUT:
                KeyboardInput?.Invoke(unchecked((uint)Native.GetMessageTime()));
                return null; // DefWindowProc освобождает данные Raw Input
            case Native.WM_TIMER:
                if (_timers.TryGetValue(wParam, out var timer))
                {
                    if (!timer.Repeat)
                    {
                        KillTimer(wParam);
                    }

                    timer.Callback();
                }

                return 0;
            case Native.WM_WTSSESSION_CHANGE:
                if (wParam is Native.WTS_SESSION_LOCK or Native.WTS_SESSION_UNLOCK)
                {
                    SessionLockChanged?.Invoke(wParam == Native.WTS_SESSION_LOCK);
                }

                return 0;
            case WM_POWERBROADCAST:
                if (wParam == PBT_APMRESUMEAUTOMATIC)
                {
                    PowerResumed?.Invoke();
                }

                return 1; // TRUE
            case Native.WM_SETTINGCHANGE:
                if (lParam != 0 && System.Runtime.InteropServices.Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                {
                    ThemeChanged?.Invoke();
                }

                return null;
        }

        if (msg == _taskbarCreated)
        {
            TaskbarCreated?.Invoke();
            return 0;
        }

        if (_ipc.TryGetValue(msg, out var name))
        {
            IpcMessage?.Invoke(name);
            return 0;
        }

        return null;
    }

    private sealed class TimerHandle(DaemonWindow owner, nuint id) : IDisposable
    {
        public void Dispose() => owner.KillTimer(id);
    }
}
