using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;

namespace SaySo.Mac.Interop;

/// <summary>
/// Главная очередь libdispatch: выполнение на главном потоке AppKit из любого потока, одноразовые и повторяющиеся таймеры.
/// Функции *_f принимают указатель на C-функцию и контекст, поэтому блоки Objective-C не нужны. Таймеры существуют, только
/// пока их держат: в простое у демона нет ни одного.
/// </summary>
internal static unsafe partial class Dispatch
{
    private const string Lib = "/usr/lib/libSystem.B.dylib";
    private const ulong TimeNow = 0;

    private static readonly nint LibHandle = NativeLibrary.Load(Lib);

    /// <summary>dispatch_get_main_queue() — макрос над этим символом.</summary>
    public static readonly nint MainQueue = NativeLibrary.GetExport(LibHandle, "_dispatch_main_q");

    private static readonly nint TimerSourceType = NativeLibrary.GetExport(LibHandle, "_dispatch_source_type_timer");

    public static bool IsMainThread => pthread_main_np() != 0;

    /// <summary>Выполнить на главном потоке (асинхронно, в порядке постановки).</summary>
    public static void Post(Action action)
    {
        var handle = GCHandle.Alloc(action);
        dispatch_async_f(MainQueue, GCHandle.ToIntPtr(handle), &RunOnce);
    }

    /// <summary>Выполнить на главном потоке и дождаться (с главного потока — сразу).</summary>
    public static void Send(Action action)
    {
        if (IsMainThread)
        {
            action();
            return;
        }

        var handle = GCHandle.Alloc(action);
        dispatch_sync_f(MainQueue, GCHandle.ToIntPtr(handle), &RunOnce);
    }

    /// <summary>Одноразовый таймер на главном потоке; Dispose до срабатывания отменяет его (и уже поставленный вызов).</summary>
    public static IDisposable After(TimeSpan delay, Action action)
    {
        var timer = new OneShot(action);
        var handle = GCHandle.Alloc(timer);
        var ns = (long)Math.Max(0, delay.TotalMilliseconds * 1_000_000);
        dispatch_after_f(dispatch_time(TimeNow, ns), MainQueue, GCHandle.ToIntPtr(handle), &RunOneShot);
        return timer;
    }

    /// <summary>Повторяющийся таймер на главном потоке (первый вызов через <paramref name="interval"/>).</summary>
    public static IDisposable Every(TimeSpan interval, Action action) => new Repeating(interval, action);

    private static void Invoke(Action action)
    {
        using var pool = AutoreleasePool.Push();
        try
        {
            action();
        }
        catch (Exception e)
        {
            // Исключение не должно уйти в libdispatch: процесс упал бы без записи в журнал.
            Log.Error("Главная очередь", e);
        }
    }

    [UnmanagedCallersOnly]
    private static void RunOnce(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var action = (Action)handle.Target!;
        handle.Free();
        Invoke(action);
    }

    [UnmanagedCallersOnly]
    private static void RunOneShot(nint context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var timer = (OneShot)handle.Target!;
        handle.Free();
        timer.Fire();
    }

    [UnmanagedCallersOnly]
    private static void RunRepeating(nint context)
    {
        var timer = (Repeating?)GCHandle.FromIntPtr(context).Target;
        timer?.Fire();
    }

    [UnmanagedCallersOnly]
    private static void FreeRepeating(nint context) => GCHandle.FromIntPtr(context).Free();

    private sealed class OneShot(Action action) : IDisposable
    {
        private volatile bool _cancelled;

        public void Fire()
        {
            if (!_cancelled)
            {
                _cancelled = true;
                Invoke(action);
            }
        }

        public void Dispose() => _cancelled = true;
    }

    private sealed class Repeating : IDisposable
    {
        private readonly Action _action;
        private nint _source;

        public Repeating(TimeSpan interval, Action action)
        {
            _action = action;
            _source = dispatch_source_create(TimerSourceType, 0, 0, MainQueue);
            if (_source == 0)
            {
                throw new InvalidOperationException("dispatch_source_create не создал таймер.");
            }

            var ns = (ulong)Math.Max(1, interval.TotalMilliseconds * 1_000_000);
            dispatch_set_context(_source, GCHandle.ToIntPtr(GCHandle.Alloc(this)));
            dispatch_set_finalizer_f(_source, &FreeRepeating); // хэндл освобождается вместе с источником
            dispatch_source_set_event_handler_f(_source, &RunRepeating);
            dispatch_source_set_timer(_source, dispatch_time(TimeNow, (long)ns), ns, ns / 10);
            dispatch_resume(_source);
        }

        public void Fire()
        {
            if (_source != 0)
            {
                Invoke(_action);
            }
        }

        public void Dispose()
        {
            var source = _source;
            if (source == 0)
            {
                return;
            }

            _source = 0;
            dispatch_source_cancel(source);
            dispatch_release(source);
        }
    }

    [LibraryImport(Lib)]
    private static partial int pthread_main_np();

    [LibraryImport(Lib)]
    private static partial void dispatch_async_f(nint queue, nint context, delegate* unmanaged<nint, void> work);

    [LibraryImport(Lib)]
    private static partial void dispatch_sync_f(nint queue, nint context, delegate* unmanaged<nint, void> work);

    [LibraryImport(Lib)]
    private static partial void dispatch_after_f(ulong when, nint queue, nint context, delegate* unmanaged<nint, void> work);

    [LibraryImport(Lib)]
    private static partial ulong dispatch_time(ulong when, long delta);

    [LibraryImport(Lib)]
    private static partial nint dispatch_source_create(nint type, nuint handle, nuint mask, nint queue);

    [LibraryImport(Lib)]
    private static partial void dispatch_set_context(nint obj, nint context);

    [LibraryImport(Lib)]
    private static partial void dispatch_set_finalizer_f(nint obj, delegate* unmanaged<nint, void> finalizer);

    [LibraryImport(Lib)]
    private static partial void dispatch_source_set_event_handler_f(nint source, delegate* unmanaged<nint, void> handler);

    [LibraryImport(Lib)]
    private static partial void dispatch_source_set_timer(nint source, ulong start, ulong interval, ulong leeway);

    [LibraryImport(Lib)]
    private static partial void dispatch_resume(nint obj);

    [LibraryImport(Lib)]
    private static partial void dispatch_source_cancel(nint source);

    [LibraryImport(Lib)]
    private static partial void dispatch_release(nint obj);
}

/// <summary>Контекст синхронизации главного потока: продолжения async возвращаются на него (как Win32SynchronizationContext).</summary>
internal sealed class MainThreadSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => Dispatch.Post(() => d(state));

    public override void Send(SendOrPostCallback d, object? state) => Dispatch.Send(() => d(state));

    public override SynchronizationContext CreateCopy() => this;
}
