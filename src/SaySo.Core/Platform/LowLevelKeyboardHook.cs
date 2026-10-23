using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
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
    private static uint s_lastEventTime;

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
        Volatile.Write(ref s_lastEventTime, unchecked((uint)Environment.TickCount)); // свежий хук ещё ничего не пропустил
        _thread = new Thread(Run) { IsBackground = true, Name = "SaySo.KeyboardHook", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _ready.Wait();
        if (_startError is not null)
        {
            Interlocked.CompareExchange(ref s_current, null, this);
            throw _startError;
        }
    }

    /// <summary>Время последнего нажатия, которое видел хук (как GetMessageTime): для <see cref="HookWatchdog"/>.</summary>
    public static uint LastEventTime => Volatile.Read(ref s_lastEventTime);

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
            Post(() => Native.PostQuitMessage(0));
            if (!_thread.Join(TimeSpan.FromSeconds(2)))
            {
                // Хук ещё установлен — не освобождаем s_current, иначе второй хук в процессе окажется возможным.
                PlatformLog.Info("Поток хука клавиатуры не завершился за 2 с.");
                return;
            }
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
                            LogOffHookThread("Действие потока хука", e); // файловый ввод-вывод на потоке хука задерживает его колбэки
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
            Interlocked.CompareExchange(ref s_current, null, this);
        }
    }

    // Журнал пишется с пула потоков: ни колбэк хука, ни цикл действий не должны ждать файл (Windows снимает медленный хук).
    private static void LogOffHookThread(string context, Exception error)
    {
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(static state => PlatformLog.Error(state.Context, state.Error), (Context: context, Error: error), preferLocal: false);
        }
        catch
        {
            // не удалось — ничего страшного, главное не пробросить исключение в нативный код
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
                Volatile.Write(ref s_lastEventTime, data->time);
                var message = (uint)wParam;
                var e = new KeyEvent(
                    (int)data->vkCode,
                    message is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN,
                    (data->flags & Native.LLKHF_INJECTED) != 0 && data->dwExtraInfo == KeyboardInjector.InjectionMarker,
                    data->time);
                if (self._onKey(e))
                {
                    return 1; // поглотить
                }
            }
            catch (Exception ex)
            {
                // Файловый ввод-вывод в колбэке хука нельзя: Windows снимает медленный хук. Пишем в журнал с пула потоков.
                LogOffHookThread("Колбэк хука", ex);
            }
        }

        return Native.CallNextHookEx(0, code, wParam, lParam);
    }
}
