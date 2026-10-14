using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class IpcTests
{
    [Fact]
    public void Send_WithoutDaemon_ReturnsFalse() =>
        Assert.False(IpcClient.Send(IpcProtocol.ReloadSettings, "SaySo.Test.NoSuchWindow." + Guid.NewGuid().ToString("N")));

    [Fact]
    public void IsDaemonRunning_WithoutDaemon_ReturnsFalse() =>
        Assert.False(IpcClient.IsDaemonRunning("SaySo.Test.NoSuchWindow." + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Send_ReachesHiddenTopLevelWindow()
    {
        var cls = "SaySo.Test.Ipc." + Guid.NewGuid().ToString("N");
        var reload = Native.RegisterWindowMessageW(IpcProtocol.ReloadSettings);
        var got = false;
        WindowAndSyncTests.OnLoopThread(() =>
        {
            using var window = new MessageWindow(cls, (msg, _, _) =>
            {
                if (msg != reload)
                {
                    return null;
                }

                got = true;
                Native.PostQuitMessage(0);
                return 0;
            }, messageOnly: false);
            Assert.True(IpcClient.IsDaemonRunning(cls));
            Assert.True(IpcClient.Send(IpcProtocol.ReloadSettings, cls));
            MessageLoop.Run();
        });
        Assert.True(got);
    }

    [Fact]
    public void Protocol_MessagesAreDistinct() =>
        Assert.Equal(IpcProtocol.AllMessages.Length, IpcProtocol.AllMessages.Distinct().Count());
}
