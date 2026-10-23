using SaySo.Core.Hotkeys;
using static SaySo.Core.Hotkeys.VirtualKeys;

namespace SaySo.Core.Tests.Hotkeys;

public class HotkeyPressWatchTests
{
    private static readonly int[] RightCtrl = [RControl];

    [Fact]
    public void FreshPress_IsReported_AutoRepeatIsNot()
    {
        var watch = new HotkeyPressWatch();
        Assert.True(watch.OnRawKey(RightCtrl, RControl, true, 1000));
        Assert.False(watch.OnRawKey(RightCtrl, RControl, true, 1500)); // автоповтор
        Assert.False(watch.OnRawKey(RightCtrl, RControl, false, 1600));
        Assert.True(watch.OnRawKey(RightCtrl, RControl, true, 5000));
    }

    [Fact]
    public void LostRelease_LaterPressIsFresh()
    {
        var watch = new HotkeyPressWatch();
        Assert.True(watch.OnRawKey(RightCtrl, RControl, true, 1000));
        Assert.True(watch.OnRawKey(RightCtrl, RControl, true, 1000 + HotkeyStateMachine.StaleRepeatMs + 1));
    }

    [Fact]
    public void OtherKeys_AreIgnored()
    {
        var watch = new HotkeyPressWatch();
        Assert.False(watch.OnRawKey(RightCtrl, LControl, true, 1000));
        Assert.False(watch.OnRawKey(RightCtrl, 0x41, true, 1100));
    }

    [Fact]
    public void Combo_ReportedWhenLastKeyArrives()
    {
        int[] combo = [LControl, LWin];
        var watch = new HotkeyPressWatch();
        Assert.False(watch.OnRawKey(combo, LControl, true, 1000));
        Assert.True(watch.OnRawKey(combo, LWin, true, 1050));
    }

    [Theory]
    [InlineData(10_000u, 10_000u, true)]
    [InlineData(10_000u, 9_500u, true)]
    [InlineData(10_000u, 2_000u, false)]
    public void HookAnswered(uint press, uint hook, bool expected) =>
        Assert.Equal(expected, HotkeyPressWatch.HookAnswered(press, hook));
}
