using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>
/// Горячая клавиша на macOS: CGEventTap на своём потоке с CFRunLoop (как поток LL-хука в Windows) и тот же
/// <see cref="HotkeyStateMachine"/>; действия уходят на главный поток. Перехват активный (не только чтение): Esc во время
/// записи поглощается. Нужен «Универсальный доступ»; без него CGEventTapCreate возвращает NULL, и конструктор бросает
/// <see cref="InvalidOperationException"/>. Если система отключила перехват (долгий обратный вызов, ввод пароля), он
/// включается обратно в том же обратном вызове.
/// </summary>
internal sealed unsafe class HotkeyTap : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(3);

    private readonly Action<HotkeyAction> _onAction;
    private readonly object _gate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    private GCHandle _self;
    private HotkeyStateMachine _machine; // под _gate
    private bool _suspended;             // под _gate
    private nint _tap;
    private nint _loop;
    private Exception? _startError;
    private volatile bool _disposed;

    public HotkeyTap(IReadOnlyList<int> keys, int tapThresholdMs, Action<HotkeyAction> onAction)
    {
        _onAction = onAction;
        _machine = Create(keys, tapThresholdMs);
        _self = GCHandle.Alloc(this);
        _thread = new Thread(Run) { IsBackground = true, Name = "SaySo.Hotkey" };
        _thread.Start();
        if (!_started.Wait(StartTimeout))
        {
            throw new InvalidOperationException("Поток перехвата клавиш не запустился вовремя.");
        }

        if (_startError is { } error)
        {
            _thread.Join(StartTimeout);
            _self.Free();
            throw error;
        }
    }

    public void Configure(IReadOnlyList<int> keys, int tapThresholdMs)
    {
        // Автомат строится на потоке вызывающего: некорректный список клавиш бросает исключение сюда.
        var machine = Create(keys.ToArray(), tapThresholdMs);
        lock (_gate)
        {
            _machine = machine;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _machine.Reset();
        }
    }

    public void SetSuspended(bool suspended)
    {
        lock (_gate)
        {
            _suspended = suspended;
            _machine.Reset();
        }
    }

    /// <summary>Перехват включён (система могла его снять, например после смены прав «Универсального доступа»).</summary>
    public bool IsEnabled => _tap != 0 && Quartz.CGEventTapIsEnabled(_tap) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var loop = Volatile.Read(ref _loop);
        if (loop != 0)
        {
            Quartz.CFRunLoopStop(loop);
            Quartz.CFRunLoopWakeUp(loop);
        }

        if (!_thread.Join(StartTimeout))
        {
            Log.Info("Поток перехвата клавиш не завершился вовремя");
            return; // хэндл не освобождаем: поток ещё может вызвать обратный вызов
        }

        _self.Free();
    }

    private static HotkeyStateMachine Create(IReadOnlyList<int> keys, int threshold) => new(keys, threshold, Keyboard.IsKeyDown);

    private void Run()
    {
        nint source = 0;
        try
        {
            var mask = Quartz.EventMask(Quartz.KeyDown, Quartz.KeyUp, Quartz.FlagsChanged);
            _tap = Quartz.CGEventTapCreate(Quartz.SessionEventTap, Quartz.HeadInsertEventTap, Quartz.TapOptionDefault, mask,
                &OnEvent, GCHandle.ToIntPtr(_self));
            if (_tap == 0)
            {
                throw new InvalidOperationException("CGEventTapCreate вернул NULL: SaySo не разрешён «Универсальный доступ».");
            }

            source = Quartz.CFMachPortCreateRunLoopSource(0, _tap, 0);
            var loop = Quartz.CFRunLoopGetCurrent();
            Quartz.CFRunLoopAddSource(loop, source, Quartz.RunLoopCommonModes);
            Quartz.CGEventTapEnable(_tap, 1);
            Volatile.Write(ref _loop, loop);
        }
        catch (Exception e)
        {
            _startError = e;
            Cleanup(source);
            _started.Set();
            return;
        }

        _started.Set();
        while (!_disposed)
        {
            using var pool = AutoreleasePool.Push();
            Quartz.CFRunLoopRunInMode(Quartz.RunLoopDefaultMode, 1.0, 0);
        }

        Cleanup(source);
    }

    private void Cleanup(nint source)
    {
        if (_tap != 0)
        {
            Quartz.CGEventTapEnable(_tap, 0);
            Quartz.CFMachPortInvalidate(_tap);
        }

        if (source != 0)
        {
            Quartz.CFRelease(source);
        }

        if (_tap != 0)
        {
            Quartz.CFRelease(_tap);
            _tap = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint OnEvent(nint proxy, int type, nint ev, nint user)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is HotkeyTap self)
            {
                return self.Handle(type, ev);
            }
        }
        catch (Exception e)
        {
            Log.Error("Перехват клавиш", e);
        }

        return ev;
    }

    private nint Handle(int type, nint ev)
    {
        if (type is Quartz.TapDisabledByTimeout or Quartz.TapDisabledByUserInput)
        {
            if (!_disposed && _tap != 0)
            {
                Quartz.CGEventTapEnable(_tap, 1);
                Log.Info(type == Quartz.TapDisabledByTimeout ? "Перехват клавиш снят системой по тайм-ауту: включён снова" : "Перехват клавиш снят вводом пользователя: включён снова");
            }

            return ev;
        }

        var code = (int)Quartz.CGEventGetIntegerValueField(ev, Quartz.KeyboardEventKeycode);
        var injected = Quartz.CGEventGetIntegerValueField(ev, Quartz.EventSourceUserData) == Keyboard.InjectedMark
                       || Quartz.CGEventGetIntegerValueField(ev, Quartz.EventSourceStateId) != Quartz.StateHidSystem;
        var now = Environment.TickCount64;
        var vk = MacKeyCodes.ToVirtualKey(code);
        HotkeyDecision decision;
        switch (type)
        {
            case Quartz.KeyDown or Quartz.KeyUp:
                decision = Feed(new KeyEvent(vk, type == Quartz.KeyDown, injected, now));
                break;
            case Quartz.FlagsChanged when MacKeyCodes.IsModifierKey(code):
                decision = Feed(new KeyEvent(vk, MacKeyCodes.IsModifierDown(code, Quartz.CGEventGetFlags(ev)), injected, now));
                break;
            case Quartz.FlagsChanged:
                // Caps Lock и прочие переключатели: macOS сообщает только о смене состояния. Для автомата это нажатие и
                // отпускание посторонней клавиши (отмена удержания); поглощать такое событие нельзя.
                var pressed = Feed(new KeyEvent(vk, true, injected, now));
                Feed(new KeyEvent(vk, false, injected, now));
                decision = pressed with { Swallow = false };
                break;
            default:
                return ev;
        }

        if (decision.Action != HotkeyAction.None)
        {
            var action = decision.Action;
            Dispatch.Post(() => _onAction(action));
        }

        return decision.Swallow ? 0 : ev;
    }

    private HotkeyDecision Feed(KeyEvent e)
    {
        lock (_gate)
        {
            return _suspended ? HotkeyDecision.Pass : _machine.OnKey(e);
        }
    }
}
