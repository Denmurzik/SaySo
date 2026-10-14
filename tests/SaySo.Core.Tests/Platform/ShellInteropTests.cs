using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class ShellInteropTests
{
    [Fact]
    public unsafe void NotifyIconData_HasNativeSize() => Assert.Equal(976, sizeof(Native.NOTIFYICONDATAW));

    [Fact]
    public void GlyphIcon_CreatesAndDestroysIcon()
    {
        var icon = GlyphIcon.Create('\uE720', 32, 0xFFFFFF, 0xE81123);
        Assert.NotEqual(0, icon);
        Assert.True(Native.DestroyIcon(icon));
    }

    [Fact]
    public void GlyphIcon_InvalidSize_ReturnsZeroWithoutThrowing() => Assert.Equal(0, GlyphIcon.Create('\uE720', 0, 0xFFFFFF, null));

    [Fact]
    public void TrayIcons_ProvideDistinctIconPerState()
    {
        using var icons = TrayIcons.Create();
        var all = Enum.GetValues<TrayState>().Select(icons.Get).ToList();
        Assert.All(all, h => Assert.NotEqual(0, h));
        Assert.Equal(all.Count, all.Distinct().Count());
    }
}
