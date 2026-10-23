using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
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
    private const uint PBT_POWERSETTINGCHANGE = 0x8013;

    private readonly MessageWindow _window;
    private readonly Dictionary<uint, string> _ipc = [];
    private readonly Dictionary<nuint, (Action Callback, bool Repeat)> _timers = [];
    private readonly uint _taskbarCreated;
    private nuint _nextTimerId = 100;
    private nint _displayNotification;
    private uint _displayState = 1; // 0 выключен, 1 включён, 2 затемнён
    private bool _rawInputSeen;

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
        unsafe
        {
            var guid = Native.GUID_CONSOLE_DISPLAY_STATE;
            _displayNotification = Native.RegisterPowerSettingNotification(_window.Handle, &guid, 0); // DEVICE_NOTIFY_WINDOW_HANDLE
        }

        if (_displayNotification == 0)
        {
            Log.Info($"RegisterPowerSettingNotification: код {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
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

    /// <summary>Нажатие или отпускание клавиши в любом окне (Raw Input): время события (как GetMessageTime), VK, нажата ли.</summary>
    public event Action<uint, int, bool>? KeyboardInput;

    /// <summary>Экран включился после выключения или затемнения (долгий простой, современный режим ожидания).</summary>
    public event Action? DisplayOn;

    /// <summary>Время последнего сообщения хука (как GetMessageTime): по нему видно, ответил ли хук на нажатие.</summary>
    public uint LastHotkeyMessageTime { get; private set; }

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
        if (_displayNotification != 0)
        {
            Native.UnregisterPowerSettingNotification(_displayNotification);
            _displayNotification = 0;
        }

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

    private unsafe void OnRawInput(nint handle)
    {
        Native.RAWINPUTKEYBOARD data;
        var size = (uint)sizeof(Native.RAWINPUTKEYBOARD);
        var read = Native.GetRawInputData(handle, Native.RID_INPUT, &data, &size, (uint)sizeof(Native.RAWINPUTHEADER));
        if (read == uint.MaxValue || read < (uint)sizeof(Native.RAWINPUTKEYBOARD) || data.header.dwType != Native.RIM_TYPEKEYBOARD)
        {
            return;
        }

        if (!_rawInputSeen)
        {
            _rawInputSeen = true;
            Log.Info("Raw Input: нажатия приходят, сторож хука работает");
        }

        var k = data.keyboard;
        if (k.ExtraInformation == (uint)KeyboardInjector.InjectionMarker || k.VKey is 0 or 0xFF)
        {
            return; // наш собственный ввод (Ctrl+V при вставке) и служебные коды без клавиши
        }

        var e0 = (k.Flags & Native.RI_KEY_E0) != 0;
        int vk = k.VKey switch
        {
            0x10 => k.MakeCode == 0x36 ? VirtualKeys.RShift : VirtualKeys.LShift,
            0x11 => e0 ? VirtualKeys.RControl : VirtualKeys.LControl,
            0x12 => e0 ? VirtualKeys.RMenu : VirtualKeys.LMenu,
            _ => k.VKey,
        };
        KeyboardInput?.Invoke(unchecked((uint)Native.GetMessageTime()), vk, (k.Flags & Native.RI_KEY_BREAK) == 0);
    }

    private unsafe void OnPowerSetting(nint lParam)
    {
        var setting = (Native.POWERBROADCAST_SETTING*)lParam;
        if (setting->PowerSetting != Native.GUID_CONSOLE_DISPLAY_STATE || setting->DataLength < sizeof(uint))
        {
            return;
        }

        var previous = _displayState;
        _displayState = setting->Data;
        if (_displayState == 1 && previous != 1)
        {
            DisplayOn?.Invoke();
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
                LastHotkeyMessageTime = unchecked((uint)Native.GetMessageTime());
                HotkeyPressed?.Invoke((HotkeyAction)wParam);
                return 0;
            case TrayMessage:
                TrayEvent?.Invoke((uint)(lParam & 0xFFFF));
                return 0;
            case Native.WM_CLOSE:
                CloseRequested?.Invoke();
                return 0;
            case Native.WM_INPUT:
                OnRawInput(lParam);
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
                else if (wParam == PBT_POWERSETTINGCHANGE && lParam != 0)
                {
                    OnPowerSetting(lParam);
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
