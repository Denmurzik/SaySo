using SaySo.Core.Updates;

namespace SaySo.Core.Tests.Updates;

/// <summary>Настоящий GitHub API: форма ответа <c>/releases/latest</c> разбирается кодом приложения. Ничего не скачивается и не запускается.</summary>
[Trait("Category", "Integration")]
public class UpdateCheckerIntegrationTests
{
    [Fact]
    public async Task LatestRelease_FromRealGitHub_IsParsedGracefully()
    {
        using var http = UpdateHttp.Create();
        var ct = TestContext.Current.CancellationToken;

        var asFuture = await new UpdateChecker(http, new Version(99, 0, 0, 0)).CheckAsync(ct);
        Assert.SkipWhen(asFuture.Outcome == UpdateCheckOutcome.Failed, $"GitHub недоступен: {asFuture.Failure}");

        // Версия из будущего: что бы ни лежало в релизах (с установщиком или без), «обновлений нет», без исключений.
        Assert.Equal(UpdateCheckOutcome.UpToDate, asFuture.Outcome);

        var asOld = await new UpdateChecker(http, new Version(0, 0, 1, 0)).CheckAsync(ct);
        Assert.Equal(UpdateCheckOutcome.Available, asOld.Outcome);
        var release = asOld.Release!;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"latest: {release.Tag}, installable={release.IsInstallable}, size={release.SetupSize}, notes={release.Notes.Length} симв.");
        Assert.True(release.Version >= new Version(0, 1, 0));
        Assert.StartsWith("https://github.com/Denmurzik/SaySo/", release.HtmlUrl);
        if (release.IsInstallable)
        {
            Assert.StartsWith("https://github.com/Denmurzik/SaySo/releases/download/", release.SetupUrl);
        }
    }
}
