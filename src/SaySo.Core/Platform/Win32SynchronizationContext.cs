using System.Collections.Concurrent;
using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>Продолжения async/await выполняются на потоке окна: Post кладёт делегат в очередь и будит окно.</summary>
internal sealed class Win32SynchronizationContext(nint hwnd, uint message) : SynchronizationContext
{
    private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state)
    {
        _queue.Enqueue((d, state));
        Native.PostMessageW(hwnd, message, 0, 0);
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == _ownerThread)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Post(s =>
        {
            try
            {
                d(s);
            }
            finally
            {
                done.Set();
            }
        }, state);
        done.Wait();
    }

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Вызывается оконной процедурой при получении <c>message</c>.</summary>
    public void Drain()
    {
        while (_queue.TryDequeue(out var item))
        {
            try
            {
                item.Callback(item.State);
            }
            catch (Exception e)
            {
                Log.Error("Продолжение async", e);
            }
        }
    }
}
