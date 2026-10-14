using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>
/// WH_KEYBOARD_LL в отдельном потоке с собственным циклом сообщений: главный поток может быть занят,
/// а медленный колбэк хука Windows молча снимает (LowLevelHooksTimeout).
/// </summary>
internal sealed unsafe class LowLevelKeyboardHook : IDisposable
{
    private const uint RunActions = Native.WM_APP + 1;
    private static LowLevelKeyboardHook? s_current;

    private readonly Func<KeyEvent, bool> _onKey;
    private readonly ConcurrentQueue<Action> _actions = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private uint _threadId;
    private Exception? _startError;

    public LowLevelKeyboardHook(Func<KeyEvent, bool> onKey)
    {
        if (Interlocked.CompareExchange(ref s_current, this, null) is not null)
        {
            throw new InvalidOperationException("Хук клавиатуры уже установлен в этом процессе.");
        }

        _onKey = onKey;
        _thread = new Thread(Run) { IsBackground = true, Name = "SaySo.KeyboardHook" };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
        {
            Interlocked.CompareExchange(ref s_current, null, this);
            throw _startError;
        }
    }

    /// <summary>Выполнить действие на потоке хука (там живёт состояние автомата).</summary>
    public void Post(Action action)
    {
        _actions.Enqueue(action);
        Native.PostThreadMessageW(_threadId, RunActions, 0, 0);
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            Native.PostThreadMessageW(_threadId, Native.WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        Interlocked.CompareExchange(ref s_current, null, this);
    }

    private void Run()
    {
        _threadId = Native.GetCurrentThreadId();
        Native.MSG msg;
        Native.PeekMessageW(&msg, 0, 0, 0, 0); // создать очередь сообщений потока до первого PostThreadMessage
        var hook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, &HookProc, Native.GetModuleHandleW(null), 0);
        if (hook == 0)
        {
            _startError = new Win32Exception(Marshal.GetLastPInvokeError(), "SetWindowsHookEx(WH_KEYBOARD_LL)");
        }

        _ready.Set();
        if (hook == 0)
        {
            return;
        }

        try
        {
            while (Native.GetMessageW(&msg, 0, 0, 0) > 0)
            {
                if (msg.message == RunActions)
                {
                    while (_actions.TryDequeue(out var action))
                    {
                        try
                        {
                            action();
                        }
                        catch (Exception e)
                        {
                            Log.Error("Действие потока хука", e);
                        }
                    }

                    continue;
                }

                Native.TranslateMessage(&msg);
                Native.DispatchMessageW(&msg);
            }
        }
        finally
        {
            Native.UnhookWindowsHookEx(hook);
        }
    }

    [UnmanagedCallersOnly]
    private static nint HookProc(int code, nuint wParam, nint lParam)
    {
        var self = s_current;
        if (code >= 0 && self is not null)
        {
            try
            {
                var data = (Native.KBDLLHOOKSTRUCT*)lParam;
                var message = (uint)wParam;
                var e = new KeyEvent(
                    (int)data->vkCode,
                    message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN,
                    data->dwExtraInfo == KeyboardInjector.InjectionMarker,
                    data->time);
                if (self._onKey(e))
                {
                    return 1; // поглотить
                }
            }
            catch (Exception ex)
            {
                Log.Error("Колбэк хука", ex);
            }
        }

        return Native.CallNextHookEx(0, code, wParam, lParam);
    }
}
