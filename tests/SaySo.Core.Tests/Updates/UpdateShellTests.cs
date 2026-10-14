using SaySo.Core.Updates;

namespace SaySo.Core.Tests.Updates;

[Collection("Log")] // при сбое запуска оболочка пишет в статический Log
public class UpdateShellTests
{
    [Fact]
    public async Task MissingSetup_IsAFailedLaunch_NotAnException()
    {
        var path = Path.Combine(Path.GetTempPath(), "sayso-tests-no-such-" + Guid.NewGuid().ToString("N"), "SaySo-Setup-9.9.9.exe");

        var run = await UpdateShell.RunInstallerAsync(path, "/VERYSILENT", TestContext.Current.CancellationToken);

        Assert.Equal(InstallerLaunchResult.Failed, run.Result);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("calc.exe")]
    [InlineData("")]
    [InlineData("http://github.com/Denmurzik/SaySo/releases/tag/v0.2.0")]
    [InlineData("https://example.test/Denmurzik/SaySo/releases/tag/v0.2.0")]
    [InlineData("https://github.com/other/repo/releases/tag/v0.2.0")]
    [InlineData("https://github.com.evil.test/Denmurzik/SaySo/releases/tag/v0.2.0")]
    public void OpenUrl_RefusesAnythingOutsideTheAllowList_WithoutTouchingTheShell(string url) =>
        Assert.False(UpdateShell.OpenUrl(url));

    [Theory]
    [InlineData("https://github.com/Denmurzik/SaySo/releases/tag/v0.2.0", true)]
    [InlineData("https://GitHub.com/denmurzik/saysO/RELEASES/download/v0.2.0/x.exe", true)]
    [InlineData("https://github.com/Denmurzik/SaySo/releases/", true)]
    [InlineData("https://github.com/Denmurzik/SaySo/releases", false)]
    [InlineData("https://github.com/Denmurzik/SaySo/", false)]
    [InlineData("https://github.com:443/Denmurzik/SaySo/releases/tag/v1.0.0", true)]
    [InlineData("https://github.com:444/Denmurzik/SaySo/releases/tag/v1.0.0", false)]
    [InlineData("https://github.com/Denmurzik/SaySo/releases/../../x", false)]
    [InlineData(null, false)]
    public void UpdateUrls_AllowListRules(string? url, bool trusted) => Assert.Equal(trusted, UpdateUrls.IsTrusted(url));

    /// <summary>Запускает настоящий процесс (cmd.exe, на мгновение появится окно консоли) — только по одному классу под блокировкой.</summary>
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ExitCodeOfTheLaunchedProcess_IsReturned_WithoutElevation()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        var run = await UpdateShell.RunInstallerAsync(cmd, "/c exit 7", TestContext.Current.CancellationToken);

        Assert.Equal(new InstallerRun(InstallerLaunchResult.Exited, 7), run);
    }
}
