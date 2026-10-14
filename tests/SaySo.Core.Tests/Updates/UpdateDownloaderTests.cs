using System.Net;
using SaySo.Core.Updates;
using static SaySo.Core.Tests.Updates.UpdateFixtures;

namespace SaySo.Core.Tests.Updates;

public class ChecksumParsingTests
{
    private const string Name = "SaySo-Setup-0.2.0.exe";
    private static readonly string Hash = new string('a', 32) + new string('0', 32);

    [Theory]
    [InlineData("{0}  SaySo-Setup-0.2.0.exe")]
    [InlineData("{0} *SaySo-Setup-0.2.0.exe")]
    [InlineData("{0}")]
    [InlineData("{0}  SaySo-Setup-0.2.0.exe\n")]
    [InlineData("{0}  SaySo-Setup-0.2.0.exe\r\n")]
    [InlineData("﻿{0}  SaySo-Setup-0.2.0.exe\r\n")]
    [InlineData("  {0}\tSaySo-Setup-0.2.0.exe  ")]
    public void Valid_ReturnsLowercaseHash(string template)
    {
        var text = string.Format(template, Hash.ToUpperInvariant());

        Assert.Equal(Hash, UpdateDownloader.ParseChecksum(text, Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("deadbeef  SaySo-Setup-0.2.0.exe")] // короткий хэш
    [InlineData("{0}  SaySo-Setup-0.3.0.exe")] // сумма другого файла
    [InlineData("{0}  SaySo-Setup-0.2.0.exe  extra")]
    [InlineData("{0}\n{0}  SaySo-Setup-0.2.0.exe")] // две строки
    [InlineData("{0}x  SaySo-Setup-0.2.0.exe")]
    [InlineData("<html>404</html>")]
    public void Invalid_ReturnsNull(string template)
    {
        var text = string.Format(template, Hash);

        Assert.Null(UpdateDownloader.ParseChecksum(text, Name));
    }

    [Fact]
    public void NonHexHash_ReturnsNull()
    {
        var text = new string('z', 64) + "  " + Name;

        Assert.Null(UpdateDownloader.ParseChecksum(text, Name));
    }
}

[Collection("Log")] // при сбоях загрузчик пишет в статический Log
public sealed class UpdateDownloaderTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly RoutingHandler _handler = new();
    private readonly byte[] _setup = SetupBytes();

    public void Dispose() => _temp.Dispose();

    private string Dir => Path.Combine(_temp.Path, "SaySo-Update");

    private string ExePath => Path.Combine(Dir, "SaySo-Setup-0.2.0.exe");

    private void Serve(byte[]? setup = null, string? sha = null)
    {
        setup ??= _setup;
        _handler.Bytes(SetupUrl("0.2.0"), setup);
        _handler.Text(ShaUrl("0.2.0"), sha ?? $"{Sha256Hex(_setup)}  SaySo-Setup-0.2.0.exe\n");
    }

    private async Task<DownloadResult> Download(ReleaseInfo? release = null)
    {
        using var http = Client(_handler);
        return await new UpdateDownloader(http, Dir).DownloadAsync(release ?? Release(setup: _setup), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ValidFile_IsStagedAndGuarded()
    {
        Serve();

        var result = await Download();

        using var installer = result.Installer!;
        Assert.Null(result.Failure);
        Assert.Equal(ExePath, installer.Path);
        Assert.Equal(_setup, File.ReadAllBytes(ExePath));
        Assert.False(File.Exists(ExePath + ".part"));
        // Пока файл проверенный и открыт стражем, его нельзя ни подменить, ни удалить.
        Assert.Throws<IOException>(() => File.WriteAllBytes(ExePath, [1, 2, 3]));
        Assert.Throws<IOException>(() => File.Delete(ExePath));
        Assert.Equal(_setup, File.ReadAllBytes(ExePath)); // читать (запускать) можно
    }

    [Fact]
    public async Task Dispose_ReleasesTheGuard_AndDeleteRemovesTheFile()
    {
        Serve();
        var installer = (await Download()).Installer!;

        installer.Dispose();
        File.WriteAllBytes(ExePath, _setup); // страж снят

        var again = (await Download()).Installer!; // повторное скачивание перезаписывает
        again.Delete();
        Assert.False(File.Exists(ExePath));
    }

    [Fact]
    public async Task HashMismatch_DeletesTheFile()
    {
        Serve(sha: $"{new string('0', 64)}  SaySo-Setup-0.2.0.exe");

        var result = await Download();

        Assert.Null(result.Installer);
        Assert.Equal(DownloadFailure.HashMismatch, result.Failure);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task TamperedBody_SameSize_IsAHashMismatch()
    {
        var tampered = (byte[])_setup.Clone();
        tampered[100] ^= 0xFF;
        Serve(setup: tampered);

        var result = await Download();

        Assert.Equal(DownloadFailure.HashMismatch, result.Failure);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task DeclaredLengthDiffersFromAsset_FailsBeforeDownloadingTheBody()
    {
        Serve(setup: new byte[_setup.Length + 10]);

        var result = await Download();

        Assert.Equal(DownloadFailure.SizeMismatch, result.Failure);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(-4999)]
    public async Task StreamLongerOrShorterThanAsset_WithoutContentLength_IsASizeMismatch(int delta)
    {
        var body = delta > 0 ? new byte[_setup.Length + delta] : _setup[..(_setup.Length + delta)];
        _handler.Route(SetupUrl("0.2.0"), _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new UnknownLengthContent(body) });
        _handler.Text(ShaUrl("0.2.0"), $"{Sha256Hex(_setup)}  SaySo-Setup-0.2.0.exe");

        var result = await Download();

        Assert.Equal(DownloadFailure.SizeMismatch, result.Failure);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task ChecksumForAnotherFile_FailsWithoutDownloadingTheSetup()
    {
        Serve(sha: $"{Sha256Hex(_setup)}  SaySo-Setup-0.3.0.exe");

        var result = await Download();

        Assert.Equal(DownloadFailure.ChecksumFileInvalid, result.Failure);
        Assert.Equal(0, _handler.Count(SetupUrl("0.2.0")));
    }

    [Fact]
    public async Task ChecksumFileMissing_IsANetworkFailure()
    {
        _handler.Bytes(SetupUrl("0.2.0"), _setup);

        var result = await Download();

        Assert.Equal(DownloadFailure.Network, result.Failure);
        Assert.Equal(0, _handler.Count(SetupUrl("0.2.0")));
    }

    [Fact]
    public async Task SetupMissing_IsANetworkFailure()
    {
        _handler.Text(ShaUrl("0.2.0"), $"{Sha256Hex(_setup)}  SaySo-Setup-0.2.0.exe");

        Assert.Equal(DownloadFailure.Network, (await Download()).Failure);
    }

    [Fact]
    public async Task ConnectionError_IsANetworkFailure()
    {
        Serve();
        _handler.Throw(SetupUrl("0.2.0"), new HttpRequestException("обрыв"));

        Assert.Equal(DownloadFailure.Network, (await Download()).Failure);
        Assert.Empty(Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task ReleaseWithoutInstaller_IsRefused()
    {
        var result = await Download(Release(installable: false));

        Assert.Equal(DownloadFailure.NotInstallable, result.Failure);
        Assert.Empty(_handler.Requests);
        Assert.False(Directory.Exists(Dir));
    }

    [Fact]
    public async Task StaleFilesFromEarlierUpdates_AreCleaned()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, "SaySo-Setup-0.1.5.exe"), "old");
        File.WriteAllText(Path.Combine(Dir, "SaySo-Setup-0.1.6.exe.part"), "partial");
        Serve();

        using var installer = (await Download()).Installer!;

        Assert.Equal([ExePath], Directory.GetFiles(Dir));
    }

    [Fact]
    public async Task TargetPathComesFromTheVersion_NotFromTheServer()
    {
        // Имя в ссылке и в ответе не влияет на путь: файл всегда SaySo-Setup-<версия>.exe в нашей папке.
        _handler.Route(SetupUrl("0.2.0"), _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(_setup)
            {
                Headers = { ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = @"..\..\evil.exe" } },
            },
        });
        _handler.Text(ShaUrl("0.2.0"), $"{Sha256Hex(_setup)}  SaySo-Setup-0.2.0.exe");

        using var installer = (await Download()).Installer!;

        Assert.Equal(ExePath, installer.Path);
        Assert.False(File.Exists(Path.Combine(_temp.Path, "evil.exe")));
    }

    [Fact]
    public async Task CallerCancellation_Propagates_AndLeavesNoPartFile()
    {
        Serve();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.Cancel();
        using var http = Client(_handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new UpdateDownloader(http, Dir).DownloadAsync(Release(setup: _setup), cts.Token));

        Assert.Empty(Directory.Exists(Dir) ? Directory.GetFiles(Dir) : []);
    }
}
