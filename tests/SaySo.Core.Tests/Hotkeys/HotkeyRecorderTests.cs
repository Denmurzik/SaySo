using SaySo.Core.Hotkeys;
using static SaySo.Core.Hotkeys.VirtualKeys;

namespace SaySo.Core.Tests.Hotkeys;

public class HotkeyRecorderTests
{
    private static KeyEvent Down(int vk) => new(vk, true, false, 0);

    private static KeyEvent Up(int vk) => new(vk, false, false, 0);

    [Fact]
    public void SingleKey()
    {
        var r = new HotkeyRecorder();
        Assert.False(r.OnKey(Down(RControl)));
        Assert.True(r.OnKey(Up(RControl)));
        Assert.Equal([RControl], r.Result);
        Assert.False(r.IsCancelled);
    }

    [Fact]
    public void Combination_InPressOrder_CompletesWhenAllReleased()
    {
        var r = new HotkeyRecorder();
        r.OnKey(Down(LControl));
        r.OnKey(Down(LWin));
        r.OnKey(Down(LWin)); // автоповтор
        Assert.False(r.OnKey(Up(LWin)));
        Assert.True(r.OnKey(Up(LControl)));
        Assert.Equal([LControl, LWin], r.Result);
    }

    [Fact]
    public void EscapeAlone_Cancels()
    {
        var r = new HotkeyRecorder();
        Assert.True(r.OnKey(Down(Escape)));
        Assert.True(r.IsCancelled);
        Assert.Null(r.Result);
    }

    [Fact]
    public void Injected_AreIgnored()
    {
        var r = new HotkeyRecorder();
        Assert.False(r.OnKey(new KeyEvent(RControl, true, true, 0)));
        Assert.False(r.OnKey(new KeyEvent(RControl, false, true, 0)));
        Assert.Null(r.Result);
    }

    [Fact]
    public void KeyUpWithoutDown_IsIgnored()
    {
        var r = new HotkeyRecorder();
        Assert.False(r.OnKey(Up(RControl)));
        Assert.Null(r.Result);
    }
}
