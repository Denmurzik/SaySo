using SaySo.Core.Hotkeys;

namespace SaySo.Core.Tests.Hotkeys;

public class HotkeyFormatterTests
{
    [Fact]
    public void RightCtrl() => Assert.Equal("Правый Ctrl", HotkeyFormatter.Format([0xA3]));

    [Fact]
    public void ModifiersFirst_InFixedOrder() =>
        Assert.Equal("Левый Ctrl + Левый Win + Пробел", HotkeyFormatter.Format([0x20, 0x5B, 0xA2]));

    [Theory]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x41, "A")]
    [InlineData(0x35, "5")]
    [InlineData(0x14, "Caps Lock")]
    [InlineData(0xFF, "0xFF")]
    public void Names(int vk, string expected) => Assert.Equal(expected, HotkeyFormatter.Name(vk));
}
