using SaySo.Core;

namespace SaySo.Core.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void NoArgs_IsDaemon() => Assert.Equal(new LaunchOptions(LaunchMode.Daemon), LaunchOptions.Parse([]));

    [Fact]
    public void UnknownArgs_AreIgnored() => Assert.Equal(LaunchMode.Daemon, LaunchOptions.Parse(["--whatever"]).Mode);

    [Fact]
    public void Settings() => Assert.Equal(LaunchMode.Settings, LaunchOptions.Parse(["--settings"]).Mode);

    [Fact]
    public void Simulate_WithOutAndInsert() =>
        Assert.Equal(new LaunchOptions(LaunchMode.Simulate, "a.wav", "r.json", true),
            LaunchOptions.Parse(["--simulate", "a.wav", "--out", "r.json", "--insert"]));

    [Fact]
    public void Simulate_WithoutWav_Throws() =>
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--simulate"]));

    [Fact]
    public void AdminTaskModes()
    {
        Assert.Equal(LaunchMode.InstallAdminTask, LaunchOptions.Parse(["--install-admin-task"]).Mode);
        Assert.Equal(LaunchMode.RemoveAdminTask, LaunchOptions.Parse(["--remove-admin-task"]).Mode);
    }

    [Fact]
    public void OverlayDemo_WithReportFileAndCycles()
    {
        Assert.Equal(new LaunchOptions(LaunchMode.OverlayDemo), LaunchOptions.Parse(["--overlay-demo"]));
        Assert.Equal(new LaunchOptions(LaunchMode.OverlayDemo, OutPath: "m.txt", Cycles: 3),
            LaunchOptions.Parse(["--overlay-demo", "--out", "m.txt", "--cycles", "3"]));
        Assert.Equal(new LaunchOptions(LaunchMode.OverlayDemo), LaunchOptions.Parse(["--overlay-demo", "--cycles", "0"]));
    }

    [Fact]
    public void CheckUpdates_WithAndWithoutReportFile()
    {
        Assert.Equal(new LaunchOptions(LaunchMode.CheckUpdates), LaunchOptions.Parse(["--check-updates"]));
        Assert.Equal(new LaunchOptions(LaunchMode.CheckUpdates, OutPath: "r.txt"), LaunchOptions.Parse(["--check-updates", "--out", "r.txt"]));
        Assert.Equal(new LaunchOptions(LaunchMode.CheckUpdates), LaunchOptions.Parse(["--check-updates", "--out"]));
    }
}
