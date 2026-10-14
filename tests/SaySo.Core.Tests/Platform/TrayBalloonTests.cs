using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class TrayBalloonTests
{
    private readonly List<(uint Message, uint Flags, string Info, string Title, uint InfoFlags, nint Icon)> _calls = [];

    private unsafe TrayIcon Create(bool addSucceeds = true) => new(0, 0x8000, null, (message, data) =>
    {
        _calls.Add((message, data->uFlags, new string(data->szInfo), new string(data->szInfoTitle), data->dwInfoFlags, data->hIcon));
        return message != Native.NIM_ADD || addSucceeds;
    });

    [Fact]
    public void ShowBalloon_ModifiesTheIcon_WithInfoText_KeepingTheIcon()
    {
        using var tray = Create();
        tray.Set(77, "SaySo");
        _calls.Clear();

        var shown = tray.ShowBalloon("SaySo", "Доступна версия 0.2.0 — нажмите, чтобы обновить");

        Assert.True(shown);
        var call = Assert.Single(_calls);
        Assert.Equal(Native.NIM_MODIFY, call.Message);
        Assert.True((call.Flags & Native.NIF_INFO) != 0);
        Assert.True((call.Flags & Native.NIF_ICON) != 0);
        Assert.Equal(77, call.Icon);
        Assert.Equal("Доступна версия 0.2.0 — нажмите, чтобы обновить", call.Info);
        Assert.Equal("SaySo", call.Title);
        Assert.True((call.InfoFlags & Native.NIIF_INFO) != 0);
        Assert.True((call.InfoFlags & Native.NIIF_RESPECT_QUIET_TIME) != 0);
    }

    [Fact]
    public void ShowBalloon_WithoutIconInTray_ReturnsFalse_AndSendsNothing()
    {
        using var tray = Create(addSucceeds: false);
        tray.Set(77, "SaySo");
        _calls.Clear();

        Assert.False(tray.ShowBalloon("SaySo", "текст"));
        Assert.Empty(_calls);
    }

    [Fact]
    public void ShowBalloon_LongText_IsCutToTheStructBuffers()
    {
        using var tray = Create();
        tray.Set(1, "SaySo");
        _calls.Clear();

        tray.ShowBalloon(new string('T', 200), new string('x', 1000));

        var call = Assert.Single(_calls);
        Assert.Equal(63, call.Title.TrimEnd('\0').Length);
        Assert.Equal(255, call.Info.TrimEnd('\0').Length);
    }

    [Fact]
    public void LaterSet_DoesNotRepeatTheBalloon()
    {
        using var tray = Create();
        tray.Set(1, "SaySo");
        tray.ShowBalloon("SaySo", "текст");
        _calls.Clear();

        tray.Set(2, "SaySo — запись…");

        var call = Assert.Single(_calls);
        Assert.Equal(0u, call.Flags & Native.NIF_INFO);
    }

    [Fact]
    public void BalloonEventCodes_MatchTheShellContract()
    {
        Assert.Equal(0x403u, Native.NIN_BALLOONHIDE);
        Assert.Equal(0x404u, Native.NIN_BALLOONTIMEOUT);
        Assert.Equal(0x405u, Native.NIN_BALLOONUSERCLICK);
    }
}
