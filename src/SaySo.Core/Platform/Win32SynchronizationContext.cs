using System.Collections.Concurrent;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>Продолжения async/await выполняются на потоке окна: Post кладёт делегат в очередь и будит окно.</summary>
internal sealed class Win32SynchronizationContext(nint hwnd, uint message) : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private int _failureLogged;
    private volatile bool _dead;

    /// <summary>Сколько ждать выполнения при <see cref="Send"/> с чужого потока.</summary>
    public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public override void Post(SendOrPostCallback d, object? state) => TryPost(d, state);

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _ownerThread)
        {
            d(state);
            return;
        }

        const int Pending = 0, Running = 1, Abandoned = 2;
        var phase = Pending;
        using var done = new ManualResetEventSlim();
        var posted = TryPost(s =>
        {
            if (Interlocked.CompareExchange(ref phase, Running, Pending) != Pending)
            {
                return; // отправитель уже отказался ждать
            }

            try
            {
                d(s);
            }
            finally
            {
                done.Set();
            }
        }, state);
        if (!posted)
        {
            Interlocked.CompareExchange(ref phase, Abandoned, Pending);
            throw new InvalidOperationException("Окно контекста синхронизации недоступно.");
        }

        if (!done.Wait(SendTimeout) && Interlocked.CompareExchange(ref phase, Abandoned, Pending) == Pending)
        {
            throw new TimeoutException("Поток окна не обработал Send вовремя.");
        }

        // Делегат уже выполняется — дожидаемся его завершения (иначе освободим done раньше Set).
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Вызывается оконной процедурой при получении <c>message</c>. Выполняет не больше элементов, чем было в очереди
    /// на входе: продолжение, которое ставит себя же заново, не вытесняет остальные сообщения окна.
    /// </summary>
    public void Drain()
    {
        for (var remaining = _queue.Count; remaining > 0 && _queue.TryDequeue(out var item); remaining--)
        {
            try
            {
                item.Callback(item.State);
            }
            catch (Exception e)
            {
                PlatformLog.Error("Продолжение async", e);
            }
        }
    }

    private bool TryPost(SendOrPostCallback d, object? state)
    {
        if (_dead)
        {
            return false;
        }

        _queue.Enqueue((d, state));
        if (Native.PostMessageW(hwnd, message, 0, 0))
        {
            return true;
        }

        if (Interlocked.Exchange(ref _failureLogged, 1) == 0)
        {
            PlatformLog.Info($"Win32SynchronizationContext: PostMessage не удался, код {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }

        if (!Native.IsWindow(hwnd))
        {
            // Окно уничтожено — выполнять некому: отбрасываем накопленное.
            _dead = true;
            _queue.Clear();
            return false;
        }

        return true; // окно живо (переполнена очередь): элемент останется и будет выполнен при следующем пробуждении
    }
}
