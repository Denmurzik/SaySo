using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class WindowAndSyncTests
{
    private static void OnLoopThread(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
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
        Assert.True(t.Join(TimeSpan.FromSeconds(10)), "Цикл сообщений не завершился.");
        if (error is not null)
        {
            throw new Xunit.Sdk.XunitException(error.ToString());
        }
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
}
