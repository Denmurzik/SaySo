using SaySo.Core.Hotkeys;

namespace SaySo.Core.Tests.Hotkeys;

public class MacKeyCodesTests
{
    [Theory]
    [InlineData(MacKeyCodes.RightCommand, VirtualKeys.RWin)]
    [InlineData(MacKeyCodes.Command, VirtualKeys.LWin)]
    [InlineData(MacKeyCodes.RightOption, VirtualKeys.RMenu)]
    [InlineData(MacKeyCodes.Option, VirtualKeys.LMenu)]
    [InlineData(MacKeyCodes.RightControl, VirtualKeys.RControl)]
    [InlineData(MacKeyCodes.Control, VirtualKeys.LControl)]
    [InlineData(MacKeyCodes.RightShift, VirtualKeys.RShift)]
    [InlineData(MacKeyCodes.Shift, VirtualKeys.LShift)]
    [InlineData(MacKeyCodes.Function, VirtualKeys.MacFunction)]
    [InlineData(MacKeyCodes.Escape, VirtualKeys.Escape)]
    [InlineData(0x31, 0x20)] // пробел
    [InlineData(0x00, 'A')]
    [InlineData(0x09, 'V')]
    [InlineData(0x1D, '0')]
    [InlineData(0x7A, 0x70)] // F1
    [InlineData(0x69, 0x7C)] // F13
    [InlineData(0x5A, 0x83)] // F20
    public void KnownKeys_MapBothWays(int keyCode, int vk)
    {
        Assert.Equal(vk, MacKeyCodes.ToVirtualKey(keyCode));
        Assert.Equal(keyCode, MacKeyCodes.FromVirtualKey(vk));
    }

    [Fact]
    public void CapsLock_IsUnmapped_SoItCancelsAndCannotBeSaved()
    {
        var vk = MacKeyCodes.ToVirtualKey(MacKeyCodes.CapsLock);

        Assert.True(vk >= MacKeyCodes.UnmappedBase);
        Assert.Equal(MacKeyCodes.CapsLock, MacKeyCodes.FromVirtualKey(vk));
    }

    [Fact]
    public void EveryKeyCode_HasADistinctVirtualKey()
    {
        var all = Enumerable.Range(0, 0x80).Select(MacKeyCodes.ToVirtualKey).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void UnknownVirtualKey_HasNoKeyCode() => Assert.Equal(-1, MacKeyCodes.FromVirtualKey(0x5D));

    [Theory]
    [InlineData(MacKeyCodes.RightCommand, 0x0010UL, true)]
    [InlineData(MacKeyCodes.RightCommand, 0x0008UL, false)] // нажат только левый Cmd
    [InlineData(MacKeyCodes.Command, 0x0008UL, true)]
    [InlineData(MacKeyCodes.RightOption, 0x0040UL, true)]
    [InlineData(MacKeyCodes.Option, 0x0040UL, false)]
    [InlineData(MacKeyCodes.RightControl, 0x2000UL, true)]
    [InlineData(MacKeyCodes.Control, 0x0001UL, true)]
    [InlineData(MacKeyCodes.RightShift, 0x0004UL, true)]
    [InlineData(MacKeyCodes.Shift, 0x0002UL, true)]
    [InlineData(MacKeyCodes.Function, 0x0080_0000UL, true)]
    [InlineData(MacKeyCodes.Function, 0UL, false)]
    public void ModifierState_ComesFromDeviceDependentBits(int keyCode, ulong flags, bool down) =>
        Assert.Equal(down, MacKeyCodes.IsModifierDown(keyCode, flags));

    [Fact]
    public void Modifiers_AreRecognized()
    {
        Assert.True(MacKeyCodes.IsModifierKey(MacKeyCodes.RightCommand));
        Assert.True(MacKeyCodes.IsModifierKey(MacKeyCodes.Function));
        Assert.False(MacKeyCodes.IsModifierKey(MacKeyCodes.CapsLock));
        Assert.False(MacKeyCodes.IsModifierKey(0x31));
    }
}
