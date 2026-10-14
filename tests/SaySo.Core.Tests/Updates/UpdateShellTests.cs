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

    [Fact]
    public void OpenUrl_IgnoresNonHttps()
    {
        // Ничего не запускается: не https отбрасывается до обращения к оболочке (иначе тест открыл бы окно/программу).
        UpdateShell.OpenUrl("file:///C:/Windows/System32/cmd.exe");
        UpdateShell.OpenUrl("calc.exe");
        UpdateShell.OpenUrl("");
    }

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
