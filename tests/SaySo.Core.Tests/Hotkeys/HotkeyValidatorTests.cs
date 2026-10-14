using SaySo.Core.Hotkeys;

namespace SaySo.Core.Tests.Hotkeys;

public class HotkeyValidatorTests
{
    [Theory]
    [InlineData(new[] { 0xA3 })]             // правый Ctrl
    [InlineData(new[] { 0xA5 })]             // правый Alt
    [InlineData(new[] { 0x14 })]             // Caps Lock
    [InlineData(new[] { 0x7C })]             // F13
    [InlineData(new[] { 0xA2, 0x5B })]       // Ctrl + Win
    [InlineData(new[] { 0xA2, 0xA0, 0x44 })] // Ctrl + Shift + D
    public void Valid(int[] keys) => Assert.Null(HotkeyValidator.Validate(keys));

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
