using SaySo.Core.Hotkeys;
using static SaySo.Core.Hotkeys.VirtualKeys;

namespace SaySo.Core.Tests.Hotkeys;

public class HotkeyStateMachineTests
{
    private const int KeyC = 0x43;

    private static KeyEvent Down(int vk, long t) => new(vk, true, false, t);

    private static KeyEvent Up(int vk, long t) => new(vk, false, false, t);

    private static HotkeyStateMachine RightCtrl() => new([RControl], 300);

    [Fact]
    public void Hold_StartsOnPress_StopsOnReleaseAfterThreshold()
    {
        var m = RightCtrl();
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(RControl, 1000)).Action);
        Assert.True(m.IsRecording);
        Assert.Equal(HotkeyAction.StopRecording, m.OnKey(Up(RControl, 1300)).Action);
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void Tap_EntersHandsFree_NextPressStops()
    {
        var m = RightCtrl();
        m.OnKey(Down(RControl, 1000));
        Assert.Equal(HotkeyAction.None, m.OnKey(Up(RControl, 1150)).Action);
        Assert.True(m.IsHandsFree);
        Assert.True(m.IsRecording);

        Assert.Equal(HotkeyAction.StopRecording, m.OnKey(Down(RControl, 5000)).Action);
        Assert.False(m.IsRecording);
        Assert.Equal(HotkeyAction.None, m.OnKey(Up(RControl, 5100)).Action);
    }

    [Fact]
    public void AutoRepeat_IsIgnored()
    {
        var m = RightCtrl();
        m.OnKey(Down(RControl, 1000));
        Assert.Equal(HotkeyAction.None, m.OnKey(Down(RControl, 1030)).Action);
        Assert.Equal(HotkeyAction.None, m.OnKey(Down(RControl, 1060)).Action);
        Assert.Equal(HotkeyAction.StopRecording, m.OnKey(Up(RControl, 1500)).Action);
    }

    [Fact]
    public void OtherKeyWhileHeld_CancelsSilently_WithoutSwallow()
    {
        var m = RightCtrl();
        m.OnKey(Down(RControl, 1000));
        var d = m.OnKey(Down(KeyC, 1050));
        Assert.Equal(HotkeyAction.Cancel, d.Action);
        Assert.False(d.Swallow);
        Assert.False(m.IsRecording);
        Assert.Equal(HotkeyAction.None, m.OnKey(Up(KeyC, 1100)).Action);
        Assert.Equal(HotkeyAction.None, m.OnKey(Up(RControl, 1200)).Action);
    }

    [Fact]
    public void EscapeWhileHeld_CancelsAndSwallows()
    {
        var m = RightCtrl();
        m.OnKey(Down(RControl, 1000));
        var d = m.OnKey(Down(Escape, 1500));
        Assert.Equal(HotkeyAction.Cancel, d.Action);
        Assert.True(d.Swallow);
    }

    [Fact]
    public void EscapeInHandsFree_CancelsAndSwallows_OtherKeysPass()
    {
        var m = RightCtrl();
        m.OnKey(Down(RControl, 1000));
        m.OnKey(Up(RControl, 1100));
        Assert.Equal(HotkeyDecision.Pass, m.OnKey(Down(KeyC, 2000)));
        Assert.Equal(HotkeyDecision.Pass, m.OnKey(Up(KeyC, 2050)));
        var d = m.OnKey(Down(Escape, 3000));
        Assert.Equal(HotkeyAction.Cancel, d.Action);
        Assert.True(d.Swallow);
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void EscapeWhenIdle_Passes() =>
        Assert.Equal(HotkeyDecision.Pass, RightCtrl().OnKey(Down(Escape, 1)));

    [Fact]
    public void ModifierAlreadyHeld_DoesNotStart()
    {
        var m = RightCtrl();
        m.OnKey(Down(LShift, 900));
        Assert.Equal(HotkeyAction.None, m.OnKey(Down(RControl, 1000)).Action);
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void Injected_AreIgnored()
    {
        var m = RightCtrl();
        Assert.Equal(HotkeyDecision.Pass, m.OnKey(new KeyEvent(RControl, true, true, 1000)));
        Assert.False(m.IsRecording);
        m.OnKey(Down(RControl, 2000));
        Assert.Equal(HotkeyDecision.Pass, m.OnKey(new KeyEvent(0x56, true, true, 2100)));
        Assert.True(m.IsRecording);
    }

    [Fact]
    public void MultiKeyCombo_StartsWhenAllDown_StopsOnFirstRelease()
    {
        var m = new HotkeyStateMachine([LControl, LWin], 300);
        Assert.Equal(HotkeyAction.None, m.OnKey(Down(LControl, 1000)).Action);
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(LWin, 1010)).Action);
        Assert.Equal(HotkeyAction.StopRecording, m.OnKey(Up(LWin, 2000)).Action);
        Assert.Equal(HotkeyAction.None, m.OnKey(Up(LControl, 2010)).Action);
    }

    [Fact]
    public void NeedsMenuMask_TrueForWinOrAlt()
    {
        Assert.False(RightCtrl().NeedsMenuMask);
        Assert.True(new HotkeyStateMachine([LControl, LWin], 300).NeedsMenuMask);
        Assert.True(new HotkeyStateMachine([RMenu], 300).NeedsMenuMask);
    }

    [Fact]
    public void TickWraparound_IsHandled()
    {
        var m = RightCtrl();
        m.OnKey(Down(RControl, uint.MaxValue - 100));
        // 150 мс спустя, но счётчик переполнился: это короткое нажатие
        Assert.Equal(HotkeyAction.None, m.OnKey(Up(RControl, 49)).Action);
        Assert.True(m.IsHandsFree);
    }

    [Fact]
    public void TickWraparound_LongHold_StopsRecording()
    {
        var m = RightCtrl();
        m.OnKey(Down(RControl, uint.MaxValue - 100));
        // 501 мс спустя через переполнение счётчика: это удержание, а не касание
        Assert.Equal(HotkeyAction.StopRecording, m.OnKey(Up(RControl, 400)).Action);
        Assert.False(m.IsRecording);
    }

    [Fact]
    public void LostKeyUp_WhileHeld_NextPressStops()
    {
        var m = RightCtrl();
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(RControl, 1000)).Action);
        // Отпускание потеряно (UAC, экран блокировки)
        Assert.Equal(HotkeyAction.StopRecording, m.OnKey(Down(RControl, 5000)).Action);
        Assert.False(m.IsRecording);
        Assert.Equal(HotkeyAction.None, m.OnKey(Up(RControl, 5100)).Action);
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(RControl, 9000)).Action);
    }

    [Fact]
    public void LostKeyUp_AfterReset_WithoutPhysicalCheck_NextPressStarts()
    {
        var m = RightCtrl();
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(RControl, 1000)).Action);
        m.Reset();
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(RControl, 5000)).Action);
    }

    [Fact]
    public void SlowAutorepeat_IsStillIgnored()
    {
        var m = RightCtrl();
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(RControl, 1000)).Action);
        Assert.Equal(HotkeyAction.None, m.OnKey(Down(RControl, 2000)).Action);
        Assert.Equal(HotkeyAction.None, m.OnKey(Down(RControl, 2033)).Action);
        Assert.Equal(HotkeyAction.None, m.OnKey(Down(RControl, 2066)).Action);
        Assert.Equal(HotkeyAction.StopRecording, m.OnKey(Up(RControl, 2500)).Action);
    }

    [Fact]
    public void Reset_ReturnsToIdle_AndPrunesStuckKeys()
    {
        var physical = new HashSet<int>();
        var m = new HotkeyStateMachine([RControl], 300, vk => physical.Contains(vk));
        physical.Add(RControl);
        m.OnKey(Down(RControl, 1000));
        // Отпускание потеряно (например, экран блокировки), физически клавиша уже отпущена.
        physical.Clear();
        m.Reset();
        Assert.False(m.IsRecording);

        physical.Add(RControl);
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(RControl, 9000)).Action);
    }

    [Fact]
    public void StaleOtherKey_IsPrunedOnNextPress()
    {
        var physical = new HashSet<int> { LShift };
        var m = new HotkeyStateMachine([RControl], 300, vk => physical.Contains(vk));
        m.OnKey(Down(LShift, 100));
        physical.Remove(LShift); // отпускание Shift потеряно
        physical.Add(RControl);
        Assert.Equal(HotkeyAction.StartRecording, m.OnKey(Down(RControl, 1000)).Action);
    }
}
