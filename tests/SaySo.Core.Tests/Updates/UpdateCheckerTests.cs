using System.Net;
using System.Text;
using SaySo.Core.Updates;
using static SaySo.Core.Tests.Updates.UpdateFixtures;

namespace SaySo.Core.Tests.Updates;

public class VersionTagTests
{
    [Theory]
    [InlineData("v0.1.1", 0, 1, 1)]
    [InlineData("V10.20.30", 10, 20, 30)]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v0.0.0", 0, 0, 0)]
    public void TryParse_AcceptsExactlyThreeNumbers(string tag, int major, int minor, int build)
    {
        Assert.True(VersionTag.TryParse(tag, out var version));
        Assert.Equal(new Version(major, minor, build), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v1")]
    [InlineData("v1.2")]
    [InlineData("v1.2.3.4")]
    [InlineData("v1.2.3-beta")]
    [InlineData("v1.2.3-rc.1")]
    [InlineData("v1.2.x")]
    [InlineData("v1..3")]
    [InlineData("v-1.2.3")]
    [InlineData("v+1.2.3")]
    [InlineData("v 1.2.3")]
    [InlineData("vv1.2.3")]
    [InlineData("v99999999999.0.0")]
    public void TryParse_RejectsEverythingElse(string? tag) => Assert.False(VersionTag.TryParse(tag, out _));

    [Fact]
    public void Normalize_MakesAssemblyVersionComparableWithTag()
    {
        // 0.1.0.0 (версия сборки) и 0.1.0 (тег) для System.Version не равны — до нормализации.
        Assert.NotEqual(new Version(0, 1, 0), new Version(0, 1, 0, 0));
        Assert.Equal(new Version(0, 1, 0), VersionTag.Normalize(new Version(0, 1, 0, 0)));
        Assert.Equal(new Version(1, 2, 0), VersionTag.Normalize(new Version(1, 2)));
    }
}

public class ReleaseParserTests
{
    private static ReleaseInfo? Parse(string json) => ReleaseParser.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Parse_FullRelease_ReadsEverything()
    {
        var release = Parse(ReleaseJson("v0.2.0", "0.2.0", size: 4321, body: "Что нового"))!;

        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal("https://github.com/Denmurzik/SaySo/releases/tag/v0.2.0", release.HtmlUrl);
        Assert.Equal("Что нового", release.Notes);
        Assert.Equal(SetupUrl("0.2.0"), release.SetupUrl);
        Assert.Equal(4321, release.SetupSize);
        Assert.Equal(ShaUrl("0.2.0"), release.ShaUrl);
        Assert.True(release.IsInstallable);
        Assert.Equal("SaySo-Setup-0.2.0.exe", release.SetupFileName);
    }

    [Fact]
    public void Parse_NotesLongerThan500_AreCut()
    {
        var release = Parse(ReleaseJson("v0.2.0", "0.2.0", body: new string('я', 900)))!;

        Assert.Equal(500, release.Notes.Length);
    }

    [Fact]
    public void Parse_NotesCutInsideSurrogatePair_DropsTheHalf()
    {
        var body = new string('a', 499) + "😀" + "tail";

        var release = Parse(ReleaseJson("v0.2.0", "0.2.0", body: body))!;

        Assert.Equal(499, release.Notes.Length);
    }

    [Fact]
    public void Parse_NullBody_GivesEmptyNotes() =>
        Assert.Equal("", Parse(ReleaseJson("v0.2.0", "0.2.0", body: null))!.Notes);

    [Fact]
    public void Parse_MissingAssets_StillAReleaseButNotInstallable()
    {
        var noSha = Parse(ReleaseJson("v0.2.0", "0.2.0", withSha: false))!;
        var noSetup = Parse(ReleaseJson("v0.2.0", "0.2.0", withSetup: false))!;
        var neither = Parse(ReleaseJson("v0.2.0", "0.2.0", withSetup: false, withSha: false))!;

        Assert.False(noSha.IsInstallable);
        Assert.False(noSetup.IsInstallable);
        Assert.False(neither.IsInstallable);
        Assert.Null(neither.SetupUrl);
        Assert.Null(neither.ShaUrl);
        Assert.Equal(0, neither.SetupSize);
    }

    [Fact]
    public void Parse_ReleaseWithoutAssetsArray_IsNotInstallable()
    {
        var release = Parse("""{"tag_name":"v0.2.0","html_url":"https://github.com/x/y/releases/tag/v0.2.0","body":"","draft":false,"prerelease":false}""")!;

        Assert.False(release.IsInstallable);
    }

    [Fact]
    public void Parse_AssetsForAnotherVersion_AreNotUsed()
    {
        // Ассеты ищутся по имени, построенному из версии тега: чужой Setup не подойдёт.
        var json = ReleaseJson("v0.2.0", "0.2.0", withSetup: false, withSha: false)
            .Replace("\"assets\": [", "\"assets\": [{\"name\":\"SaySo-Setup-0.3.0.exe\",\"size\":1,\"browser_download_url\":\"https://example.test/x.exe\"},");

        Assert.False(Parse(json)!.IsInstallable);
    }

    [Theory]
    [InlineData("http://example.test/dl/SaySo-Setup-0.2.0.exe")]
    [InlineData("file:///C:/evil/SaySo-Setup-0.2.0.exe")]
    [InlineData("not a url")]
    public void Parse_NonHttpsAssetUrl_IsDropped(string url)
    {
        var json = ReleaseJson("v0.2.0", "0.2.0").Replace(SetupUrl("0.2.0"), url);

        var release = Parse(json)!;

        Assert.Null(release.SetupUrl);
        Assert.False(release.IsInstallable);
    }

    [Fact]
    public void Parse_NonHttpsReleasePage_MeansNoUsableRelease() =>
        Assert.Null(Parse(ReleaseJson("v0.2.0", "0.2.0", htmlUrl: "http://github.com/x/TAG")));

    [Theory]
    [InlineData("latest")]
    [InlineData("v0.2")]
    [InlineData("v0.2.0-beta.1")]
    [InlineData("nightly-2026-10-14")]
    public void Parse_UnparsableTag_IsIgnored(string tag) => Assert.Null(Parse(ReleaseJson(tag, "0.2.0")));

    [Fact]
    public void Parse_PrereleaseAndDraft_AreIgnored()
    {
        Assert.Null(Parse(ReleaseJson("v0.2.0", "0.2.0", prerelease: true)));
        Assert.Null(Parse(ReleaseJson("v0.2.0", "0.2.0", draft: true)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("<html>rate limit</html>")]
    [InlineData("{\"tag_name\":")]
    public void Parse_NotAReleaseObject_Throws(string json) =>
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Parse(json));
}

public class UpdateCheckerTests
{
    private static readonly Version Installed = new(0, 1, 0, 0); // как версия сборки при <Version>0.1.0</Version>

    private static async Task<UpdateCheckResult> Check(RoutingHandler handler, Version? current = null)
    {
        using var http = Client(handler);
        return await new UpdateChecker(http, current ?? Installed).CheckAsync(TestContext.Current.CancellationToken);
    }

    private static RoutingHandler Latest(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new RoutingHandler();
        handler.Json(UpdateChecker.LatestReleaseUrl, json, status);
        return handler;
    }

    [Fact]
    public async Task NewerRelease_IsAvailable()
    {
        var result = await Check(Latest(ReleaseJson("v0.1.1", "0.1.1")));

        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.Equal(new Version(0, 1, 1), result.Release!.Version);
        Assert.True(result.Release.IsInstallable);
    }

    [Fact]
    public async Task SameVersionAsAssembly_IsUpToDate_EvenWithRevisionZero() =>
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await Check(Latest(ReleaseJson("v0.1.0", "0.1.0")))).Outcome);

    [Fact]
    public async Task OlderRelease_IsUpToDate() =>
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await Check(Latest(ReleaseJson("v0.0.9", "0.0.9")))).Outcome);

    [Fact]
    public async Task ComparisonIsNumericNotLexical()
    {
        var result = await Check(Latest(ReleaseJson("v0.10.0", "0.10.0")), current: new Version(0, 9, 0));

        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
    }

    [Fact]
    public async Task RealWorldV010_WithoutSetupAsset_AgainstV010Build_IsUpToDate()
    {
        // Текущий релиз v0.1.0 на GitHub: только ZIP, установщика нет.
        var result = await Check(Latest(ReleaseJson("v0.1.0", "0.1.0", withSetup: false, withSha: false)));

        Assert.Equal(UpdateCheckOutcome.UpToDate, result.Outcome);
    }

    [Fact]
    public async Task NewerReleaseWithoutInstaller_IsAvailableButNotInstallable()
    {
        var result = await Check(Latest(ReleaseJson("v0.2.0", "0.2.0", withSetup: false, withSha: false)));

        Assert.Equal(UpdateCheckOutcome.Available, result.Outcome);
        Assert.False(result.Release!.IsInstallable);
    }

    [Theory]
    [InlineData("latest")]
    [InlineData("v9.9")]
    [InlineData("v9.9.9-beta")]
    public async Task UnparsableTag_IsUpToDate_NotAnError(string tag) =>
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await Check(Latest(ReleaseJson(tag, "9.9.9")))).Outcome);

    [Fact]
    public async Task Prerelease_IsNotAnUpdate() =>
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await Check(Latest(ReleaseJson("v9.9.9", "9.9.9", prerelease: true)))).Outcome);

    [Fact]
    public async Task SendsUserAgentAndAcceptHeaders()
    {
        var handler = Latest(ReleaseJson("v0.1.0", "0.1.0"));

        await Check(handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(UpdateChecker.LatestReleaseUrl, request.Url);
        Assert.Equal("SaySo/0.1.0", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
    }

    [Fact]
    public async Task NoReleasesYet_404_IsUpToDate() =>
        Assert.Equal(UpdateCheckOutcome.UpToDate, (await Check(new RoutingHandler())).Outcome);

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UpdateFailure.RateLimited)]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateFailure.RateLimited)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFailure.Network)]
    [InlineData(HttpStatusCode.BadGateway, UpdateFailure.Network)]
    [InlineData(HttpStatusCode.Unauthorized, UpdateFailure.BadResponse)]
    public async Task HttpErrors_AreMappedToFailures(HttpStatusCode status, UpdateFailure expected)
    {
        var result = await Check(Latest("{}", status));

        Assert.Equal(UpdateCheckOutcome.Failed, result.Outcome);
        Assert.Equal(expected, result.Failure);
    }

    [Fact]
    public async Task NetworkException_IsAFailureNotAThrow()
    {
        var handler = new RoutingHandler();
        handler.Throw(UpdateChecker.LatestReleaseUrl, new HttpRequestException("нет сети"));

        var result = await Check(handler);

        Assert.Equal(UpdateFailure.Network, result.Failure);
    }

    [Fact]
    public async Task GarbageResponse_IsBadResponse() =>
        Assert.Equal(UpdateFailure.BadResponse, (await Check(Latest("<html>oops</html>"))).Failure);

    [Fact]
    public async Task HugeResponse_IsBadResponse() =>
        Assert.Equal(UpdateFailure.BadResponse, (await Check(Latest("{\"body\":\"" + new string('x', 2 * 1024 * 1024) + "\"}"))).Failure);

    [Fact]
    public async Task SlowServer_TimesOut()
    {
        using var http = new HttpClient(new HangingHandler());

        var result = await new UpdateChecker(http, Installed, timeout: TimeSpan.FromMilliseconds(100)).CheckAsync(TestContext.Current.CancellationToken);

        Assert.Equal(UpdateFailure.Timeout, result.Failure);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var http = new HttpClient(new HangingHandler());
        var task = new UpdateChecker(http, Installed).CheckAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void CountsAsAttempt_IsFalseOnlyForConnectivityFailures()
    {
        Assert.True(UpdateCheckResult.UpToDate.CountsAsAttempt);
        Assert.True(UpdateCheckResult.Available(Release()).CountsAsAttempt);
        Assert.True(UpdateCheckResult.Failed(UpdateFailure.RateLimited).CountsAsAttempt);
        Assert.True(UpdateCheckResult.Failed(UpdateFailure.BadResponse).CountsAsAttempt);
        Assert.False(UpdateCheckResult.Failed(UpdateFailure.Network).CountsAsAttempt);
        Assert.False(UpdateCheckResult.Failed(UpdateFailure.Timeout).CountsAsAttempt);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
    }
}
