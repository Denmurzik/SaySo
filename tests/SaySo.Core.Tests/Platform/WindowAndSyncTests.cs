using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class WindowAndSyncTests
{
    /// <summary>
    /// Выполняет тело в STA-потоке с циклом сообщений: исключения переносятся в тест, а зависший цикл
    /// принудительно завершается через WM_QUIT, чтобы не повесить хост тестов.
    /// </summary>
    internal static void OnLoopThread(Action body)
    {
        Exception? error = null;
        uint threadId = 0;
        var t = new Thread(() =>
        {
            threadId = Native.GetCurrentThreadId();
            try
            {
                body();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(10)))
        {
            Native.PostThreadMessageW(threadId, Native.WM_QUIT, 0, 0);
            t.Join(TimeSpan.FromSeconds(2));
            throw new Xunit.Sdk.XunitException("Цикл сообщений не завершился.");
        }

        if (error is not null)
        {
            throw new Xunit.Sdk.XunitException(error.ToString());
        }
    }

    private static Exception? SendFromForeignThread(Win32SynchronizationContext ctx, Action action)
    {
        Exception? caught = null;
        var t = new Thread(() =>
        {
            try
            {
                ctx.Send(_ => action(), null);
            }
            catch (Exception e)
            {
                caught = e;
            }
        });
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "Send завис.");
        return caught;
    }

    [Fact]
    public void MessageWindow_ReceivesPostedMessage_AndLoopExits()
    {
        const uint Ping = Native.WM_APP + 5;
        var received = false;
        OnLoopThread(() =>
        {
            using var window = new MessageWindow("SaySo.Test.Ping." + Guid.NewGuid().ToString("N"), (msg, w, l) =>
            {
                if (msg != Ping)
                {
                    return null;
                }

                received = w == 42;
                Native.PostQuitMessage(7);
                return 0;
            }, messageOnly: true);
            Native.PostMessageW(window.Handle, Ping, 42, 0);
            Assert.Equal(7, MessageLoop.Run());
        });
        Assert.True(received);
    }

    [Fact]
    public void SyncContext_PostFromOtherThread_RunsOnLoopThread()
    {
        const uint Sync = Native.WM_APP + 6;
        int loopThread = 0, ranOn = 0;
        OnLoopThread(() =>
        {
            loopThread = Environment.CurrentManagedThreadId;
            Win32SynchronizationContext? ctx = null;
            using var window = new MessageWindow("SaySo.Test.Sync." + Guid.NewGuid().ToString("N"), (msg, _, _) =>
            {
                if (msg != Sync)
                {
                    return null;
                }

                ctx!.Drain();
                return 0;
            }, messageOnly: true);
            ctx = new Win32SynchronizationContext(window.Handle, Sync);
            ThreadPool.QueueUserWorkItem(_ => ctx.Post(_ =>
            {
                ranOn = Environment.CurrentManagedThreadId;
                Native.PostQuitMessage(0);
            }, null));
            MessageLoop.Run();
        });
        Assert.Equal(loopThread, ranOn);
    }

    [Fact]
    public void ExceptionInHandler_DoesNotCrash()
    {
        const uint Boom = Native.WM_APP + 7;
        OnLoopThread(() =>
        {
            using var window = new MessageWindow("SaySo.Test.Boom." + Guid.NewGuid().ToString("N"), (msg, _, _) =>
            {
                if (msg == Boom)
                {
                    Native.PostQuitMessage(0);
                    throw new InvalidOperationException("ошибка в обработчике");
                }

                return null;
            }, messageOnly: true);
            Native.PostMessageW(window.Handle, Boom, 0, 0);
            MessageLoop.Run();
        });
    }

    [Fact]
    public void Send_FromForeignThread_AfterWindowDestroyed_Throws()
    {
        const uint Sync = Native.WM_APP + 8;
        var window = new MessageWindow("SaySo.Test.Dead." + Guid.NewGuid().ToString("N"), (_, _, _) => null, messageOnly: true);
        var ctx = new Win32SynchronizationContext(window.Handle, Sync);
        window.Dispose();
        var ran = false;
        var error = SendFromForeignThread(ctx, () => ran = true);
        Assert.IsType<InvalidOperationException>(error);
        Assert.False(ran);
    }

    [Fact]
    public void Send_FromForeignThread_WhenLoopNotPumping_TimesOutAndIsAbandoned()
    {
        const uint Sync = Native.WM_APP + 8;
        using var window = new MessageWindow("SaySo.Test.Stall." + Guid.NewGuid().ToString("N"), (_, _, _) => null, messageOnly: true);
        var ctx = new Win32SynchronizationContext(window.Handle, Sync) { SendTimeout = TimeSpan.FromMilliseconds(200) };
        var ran = false;
        var error = SendFromForeignThread(ctx, () => ran = true);
        Assert.IsType<TimeoutException>(error);
        ctx.Drain(); // отказавшийся ждать Send не должен выполниться позже
        Assert.False(ran);
    }

    [Fact]
    public void Drain_DoesNotRunItemsPostedDuringSameDrain()
    {
        const uint Sync = Native.WM_APP + 8;
        using var window = new MessageWindow("SaySo.Test.Drain." + Guid.NewGuid().ToString("N"), (_, _, _) => null, messageOnly: true);
        var ctx = new Win32SynchronizationContext(window.Handle, Sync);
        int first = 0, second = 0;
        ctx.Post(_ =>
        {
            first++;
            ctx.Post(_ => second++, null);
        }, null);
        ctx.Drain();
        Assert.Equal((1, 0), (first, second));
        ctx.Drain();
        Assert.Equal((1, 1), (first, second));
    }

    [Fact]
    public void WindowDestroyedExternally_ResetsHandle_AndDisposeIsNoOp()
    {
        var window = new MessageWindow("SaySo.Test.Ncd." + Guid.NewGuid().ToString("N"), (_, _, _) => null, messageOnly: true);
        Assert.NotEqual(0, window.Handle);
        Native.DestroyWindow(window.Handle);
        Assert.Equal(0, window.Handle);
        window.Dispose();
    }
}
