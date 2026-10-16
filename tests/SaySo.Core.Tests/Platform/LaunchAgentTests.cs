using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class LaunchAgentTests
{
    private const string BundleExe = "/Applications/SaySo.app/Contents/MacOS/SaySo";

    [Fact]
    public void Bundle_IsStartedThroughLaunchServices_InBackground()
    {
        var plist = LaunchAgent.Build(BundleExe);

        Assert.Contains("<string>io.github.denmurzik.sayso</string>", plist);
        Assert.Contains("<string>/usr/bin/open</string>\n\t\t<string>-g</string>\n\t\t<string>-a</string>\n\t\t<string>/Applications/SaySo.app</string>", plist);
        Assert.Contains("<key>RunAtLoad</key>\n\t<true/>", plist);
        Assert.Contains("<string>Aqua</string>", plist);
    }

    [Fact]
    public void ExecutableOutsideABundle_IsStartedDirectly() =>
        Assert.Contains("<array>\n\t\t<string>/Users/dev/sayso/SaySo</string>\n\t</array>", LaunchAgent.Build("/Users/dev/sayso/SaySo"));

    [Fact]
    public void PathIsXmlEscaped() =>
        Assert.Contains("/Users/a&amp;b/SaySo.app", LaunchAgent.Build("/Users/a&b/SaySo.app/Contents/MacOS/SaySo"));

    [Theory]
    [InlineData(BundleExe, "/Applications/SaySo.app")]
    [InlineData("/Users/x/Apps/My SaySo.app/Contents/MacOS/SaySo", "/Users/x/Apps/My SaySo.app")]
    [InlineData("/Users/x/SaySo", null)]
    [InlineData("/Users/x/Contents/MacOS/SaySo", null)]
    public void AppBundleOf(string exe, string? bundle) => Assert.Equal(bundle, LaunchAgent.AppBundleOf(exe));

    [Fact]
    public void Set_WritesAndRemovesThePlist_ForThisCopyOnly()
    {
        using var dir = new TempDir();
        var agent = new LaunchAgent(dir.Path);

        Assert.False(agent.IsEnabled(BundleExe));
        agent.Set(true, BundleExe);
        Assert.True(agent.IsEnabled(BundleExe));
        Assert.False(agent.IsEnabled("/Other/SaySo.app/Contents/MacOS/SaySo"));
        Assert.Equal(LaunchAgent.Build(BundleExe), File.ReadAllText(agent.PlistPath));

        agent.Set(false, BundleExe);
        Assert.False(File.Exists(agent.PlistPath));
        agent.Set(false, BundleExe); // уже выключен — без ошибок
    }
}
