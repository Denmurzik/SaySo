using SaySo.Core.Hotkeys;

namespace SaySo.Core.Tests.Hotkeys;

public class HotkeyFormatterTests
{
    [Fact]
    public void RightCtrl() =>
        Assert.Equal(OperatingSystem.IsMacOS() ? "Правый Control" : "Правый Ctrl", HotkeyFormatter.Format([0xA3]));

    [Fact]
    public void ModifiersFirst_InFixedOrder() =>
        Assert.Equal(OperatingSystem.IsMacOS() ? "Левый Control + Левый Cmd + Пробел" : "Левый Ctrl + Левый Win + Пробел",
            HotkeyFormatter.Format([0x20, 0x5B, 0xA2]));

    [Theory]
    [InlineData(0xA3, "Правый Control")]
    [InlineData(0xA5, "Правый Option")]
    [InlineData(0x5C, "Правый Cmd")]
    [InlineData(0x5B, "Левый Cmd")]
    [InlineData(0x8F, "Fn")]
    [InlineData(0xA0, "Левый Shift")]
    [InlineData(0x70, "F1")]
    public void MacNames(int vk, string expected) => Assert.Equal(expected, HotkeyFormatter.MacName(vk));

    [Theory]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x41, "A")]
    [InlineData(0x35, "5")]
    [InlineData(0x14, "Caps Lock")]
    [InlineData(0xFF, "0xFF")]
    public void Names(int vk, string expected) => Assert.Equal(expected, HotkeyFormatter.Name(vk));
}
