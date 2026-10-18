using SaySo.Core.Hotkeys;

namespace SaySo.Core.Tests.Hotkeys;

public class HotkeyValidatorTests
{
    [Theory]
    [InlineData(new[] { 0xA3 })]             // правый Ctrl
    [InlineData(new[] { 0xA5 })]             // правый Alt
    [InlineData(new[] { 0x7C })]             // F13
    [InlineData(new[] { 0xA2, 0x5B })]       // Ctrl + Win
    [InlineData(new[] { 0xA2, 0xA0, 0x44 })] // Ctrl + Shift + D
    public void Valid(int[] keys) => Assert.Null(HotkeyValidator.Validate(keys));

    // Caps Lock: в Windows отдельная клавиша, а macOS сообщает только о переключении его состояния (удерживать нельзя).
    [Fact]
    public void CapsLock_ValidOnlyOnWindows() =>
        Assert.Equal(OperatingSystem.IsMacOS(), HotkeyValidator.Validate([0x14]) is not null);

    [Fact]
    public void MacFn_AloneAndInCombos_ValidOnlyOnMac()
    {
        Assert.Equal(!OperatingSystem.IsMacOS(), HotkeyValidator.Validate([VirtualKeys.MacFunction]) is not null);
        Assert.Equal(!OperatingSystem.IsMacOS(), HotkeyValidator.Validate([VirtualKeys.MacFunction, 0x20]) is not null);
    }

    [Fact]
    public void Advice_NamesThePlatformKeys() =>
        Assert.Contains(OperatingSystem.IsMacOS() ? "правый Cmd" : "правый Ctrl", HotkeyValidator.Validate([0x41]));

    [Fact]
    public void Empty_IsInvalid() => Assert.NotNull(HotkeyValidator.Validate([]));

    [Fact]
    public void Escape_IsReserved() => Assert.Contains("Esc", HotkeyValidator.Validate([0xA2, 0x1B]));

    [Fact]
    public void SingleLetter_IsInvalid() => Assert.NotNull(HotkeyValidator.Validate([0x41]));

    [Fact]
    public void SingleShift_IsInvalid() => Assert.NotNull(HotkeyValidator.Validate([0xA0]));

    [Fact]
    public void ShiftPlusLetter_IsInvalid() => Assert.NotNull(HotkeyValidator.Validate([0xA0, 0x41]));

    [Fact]
    public void TooManyKeys_IsInvalid() => Assert.NotNull(HotkeyValidator.Validate([0xA2, 0xA0, 0xA4, 0x5B, 0x41]));
}
