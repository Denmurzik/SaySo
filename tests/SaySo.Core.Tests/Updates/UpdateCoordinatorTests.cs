using System.ComponentModel;
using System.Net;
using SaySo.Core.Overlay;
using SaySo.Core.Storage;
using SaySo.Core.Updates;
using static SaySo.Core.Tests.Updates.UpdateFixtures;

namespace SaySo.Core.Tests.Updates;

internal sealed class FakeUpdateHost : IUpdateHost
{
    public bool Idle { get; set; } = true;

    public bool IsDictationIdle => Idle;

    public List<(TimeSpan Delay, Action Action, bool Disposed)> Timers { get; } = [];

    public bool TimerFails { get; set; }

    public List<string> Balloons { get; } = [];

    public List<(string Text, bool IsError)> Messages { get; } = [];

    public List<ReleaseInfo?> PendingChanges { get; } = [];

    public List<string> OpenedUrls { get; } = [];

    public List<(string Path, string Arguments, bool Elevate)> Launches { get; } = [];

    public InstallerLaunchResult LaunchResult { get; set; } = InstallerLaunchResult.Started;

    /// <summary>Содержимое установщика в момент запуска (файл сразу после запуска может быть удалён).</summary>
    public byte[]? LaunchedContent { get; private set; }

    public int Quits { get; private set; }

    public int Finished { get; private set; }

    public Action? OnLaunch { get; set; }

    public IDisposable ScheduleOnce(TimeSpan delay, Action action)
    {
        if (TimerFails)
        {
            throw new Win32Exception(8, "SetTimer");
        }

        Timers.Add((delay, action, false));
        var index = Timers.Count - 1;
        return new Handle(() => Timers[index] = Timers[index] with { Disposed = true });
    }

    public void ShowBalloon(string text) => Balloons.Add(text);

    public void ShowMessage(string text, bool isError) => Messages.Add((text, isError));

    public void PendingChanged(ReleaseInfo? release) => PendingChanges.Add(release);

    public void OpenUrl(string url) => OpenedUrls.Add(url);

    public InstallerLaunchResult LaunchInstaller(string path, string arguments, bool elevate)
    {
        Launches.Add((path, arguments, elevate));
        LaunchedContent = File.ReadAllBytes(path);
        OnLaunch?.Invoke();
        return LaunchResult;
    }

    public void Quit() => Quits++;

    public void OperationFinished() => Finished++;

    public void FireTimer(int index = -1)
    {
        var (_, action, disposed) = Timers[index < 0 ? Timers.Count + index : index];
        Assert.False(disposed);
        action();
    }

    private sealed class Handle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

[Collection("Log")] // координатор пишет в статический Log
public sealed class UpdateCoordinatorTests : IDisposable
{
    private static readonly Version Installed = new(0, 1, 0, 0);

    private readonly TempDir _temp = new();
    private readonly FakeUpdateHost _host = new();
    private readonly RoutingHandler _handler = new();
    private readonly ManualTimeProvider _time = new();
    private readonly byte[] _setup = SetupBytes();
    private readonly AppPaths _paths;
    private readonly UpdateStateStore _store;
    private bool _autoEnabled = true;
    private string _exePath;
    private IReadOnlyList<string> _programFiles = [@"C:\Program Files"];
    private UpdateCoordinator? _coordinator;

    public UpdateCoordinatorTests()
    {
        _paths = new AppPaths(Path.Combine(_temp.Path, "data"));
        _paths.EnsureRoot();
        _store = new UpdateStateStore(_paths);
        _exePath = MakeInstall(Path.Combine(_temp.Path, "Local", "Programs", "SaySo"));
    }

    public void Dispose()
    {
        _coordinator?.Dispose();
        _temp.Dispose();
    }

    private UpdateCoordinator Coordinator => _coordinator ??= new UpdateCoordinator(
        _host, _store, Installed,
        new UpdateEnvironment(_exePath, _programFiles, Path.Combine(_temp.Path, "SaySo-Update")),
        () => _autoEnabled, () => Client(_handler), _time);

    /// <summary>Каталог «установленного» SaySo: рядом с exe лежит деинсталлятор Inno.</summary>
    private static string MakeInstall(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "unins000.exe"), "uninstaller");
        return Path.Combine(dir, "SaySo.exe");
    }

    private void ServeRelease(string version = "0.2.0", bool installable = true)
    {
        _handler.Json(UpdateChecker.LatestReleaseUrl, ReleaseJson("v" + version, version, withSetup: installable, withSha: installable, size: _setup.Length));
        if (installable)
        {
            _handler.Bytes(SetupUrl(version), _setup);
            _handler.Text(ShaUrl(version), $"{Sha256Hex(_setup)}  SaySo-Setup-{version}.exe\n");
        }
    }

    private static async Task Settle(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(done(), "операция не завершилась");
    }

    private void StartAndFire()
    {
        Coordinator.OnStart();
        _host.FireTimer();
    }

    // ---- автоматическая проверка ----

    [Fact]
    public async Task Start_SchedulesOneShotCheck_After60Seconds_AndShowsBalloonForNewVersion()
    {
        ServeRelease();

        Coordinator.OnStart();
        var timer = Assert.Single(_host.Timers);
        Assert.Equal(TimeSpan.FromSeconds(60), timer.Delay);
        Assert.Empty(_handler.Requests); // до срабатывания таймера сети нет

        _host.FireTimer();
        await Settle(() => _host.Balloons.Count > 0);

        Assert.Equal("Доступна версия 0.2.0 — нажмите, чтобы обновить", Assert.Single(_host.Balloons));
        Assert.Equal(new Version(0, 2, 0), Coordinator.Pending!.Version);
        Assert.Equal(new Version(0, 2, 0), _host.PendingChanges[^1]!.Version);
        Assert.True(_host.Timers[0].Disposed); // одноразовый таймер снят после срабатывания
        Assert.Equal(_time.GetUtcNow(), _store.Load().LastCheckUtc);
    }

    [Fact]
    public void Start_WithRecentCheck_SchedulesNothing()
    {
        _store.Save(new UpdateState { LastCheckUtc = _time.GetUtcNow() - TimeSpan.FromHours(23) });

        Coordinator.OnStart();
        Coordinator.OnResume();
        Coordinator.OnUnlock();

        Assert.Empty(_host.Timers);
    }

    [Fact]
    public void Start_WithCheckOlderThan24h_Schedules()
    {
        _store.Save(new UpdateState { LastCheckUtc = _time.GetUtcNow() - TimeSpan.FromHours(24) });

        Coordinator.OnStart();

        Assert.Single(_host.Timers);
    }

    [Fact]
    public void Events_WhenAutoCheckDisabled_ScheduleNothing()
    {
        _autoEnabled = false;

        Coordinator.OnStart();
        Coordinator.OnResume();
        Coordinator.OnUnlock();

        Assert.Empty(_host.Timers);
    }

    [Fact]
    public void ResumeAndUnlock_ShareOneTimer_WithShortDelay()
    {
        Coordinator.OnResume();
        Coordinator.OnUnlock();
        Coordinator.OnStart();

        var timer = Assert.Single(_host.Timers);
        Assert.Equal(TimeSpan.FromSeconds(15), timer.Delay);
    }

    [Fact]
    public async Task SettingTurnedOffBeforeTimerFires_CancelsTheCheck()
    {
        ServeRelease();
        Coordinator.OnStart();
        _autoEnabled = false;

        _host.FireTimer();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task ManualCheckBeforeTimerFires_MakesTheAutoCheckUnnecessary()
    {
        ServeRelease();
        Coordinator.OnStart();
        _store.Save(new UpdateState { LastCheckUtc = _time.GetUtcNow() });

        _host.FireTimer();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void TimerThatFailsToStart_IsNotFatal()
    {
        _host.TimerFails = true;

        Coordinator.OnStart(); // не бросает

        Assert.Empty(_host.Timers);
    }

    [Fact]
    public async Task CheckDuringDictation_WaitsForSessionEnd()
    {
        ServeRelease();
        Coordinator.OnStart();
        _host.Idle = false;

        _host.FireTimer();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Empty(_handler.Requests); // во время диктовки к GitHub не ходим

        _host.Idle = true;
        Coordinator.OnSessionEnded();
        await Settle(() => _host.Balloons.Count > 0);

        Assert.Equal(1, _handler.Count(UpdateChecker.LatestReleaseUrl));
    }

    [Fact]
    public async Task SessionEnded_WhileStillNotIdle_DoesNothing()
    {
        ServeRelease();
        Coordinator.OnStart();
        _host.Idle = false;
        _host.FireTimer();

        Coordinator.OnSessionEnded();
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task BalloonIsDeferredWhenDictationStartsDuringTheCheck()
    {
        ServeRelease();
        _handler.Route(UpdateChecker.LatestReleaseUrl, _ =>
        {
            _host.Idle = false; // пользователь начал диктовать, пока шёл запрос
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ReleaseJson("v0.2.0", "0.2.0", size: _setup.Length)),
            };
        });
        StartAndFire();
        await Settle(() => _host.Finished > 0);
        Assert.Empty(_host.Balloons);

        _host.Idle = true;
        Coordinator.OnSessionEnded();

        Assert.Single(_host.Balloons);
    }

    [Fact]
    public async Task NoNewVersion_NoBalloon_PendingStaysNull()
    {
        _handler.Json(UpdateChecker.LatestReleaseUrl, ReleaseJson("v0.1.0", "0.1.0", withSetup: false, withSha: false));

        StartAndFire();
        await Settle(() => _host.Finished > 0);

        Assert.Empty(_host.Balloons);
        Assert.Null(Coordinator.Pending);
        Assert.Null(_host.PendingChanges[^1]);
    }

    [Fact]
    public async Task NetworkFailure_IsSilent_AndDoesNotConsumeTheDailyAttempt()
    {
        _handler.Throw(UpdateChecker.LatestReleaseUrl, new HttpRequestException("нет сети"));

        StartAndFire();
        await Settle(() => _host.Finished > 0);

        Assert.Empty(_host.Balloons);
        Assert.Empty(_host.Messages);
        Assert.Null(_store.Load().LastCheckUtc);
        Coordinator.OnUnlock(); // по следующему событию проверка повторяется
        Assert.Equal(2, _host.Timers.Count);
    }

    [Fact]
    public async Task RateLimit_CountsAsAnAttempt()
    {
        _handler.Status(UpdateChecker.LatestReleaseUrl, HttpStatusCode.Forbidden);

        StartAndFire();
        await Settle(() => _host.Finished > 0);

        Assert.NotNull(_store.Load().LastCheckUtc);
        Coordinator.OnUnlock();
        Assert.Single(_host.Timers); // повторной попытки до конца суток нет
    }

    // ---- уведомление ----

    [Fact]
    public async Task BalloonClosed_RemembersDismissedVersion_AndNextCheckOnlyUpdatesMenu()
    {
        ServeRelease();
        StartAndFire();
        await Settle(() => _host.Balloons.Count == 1);

        Coordinator.OnBalloonClosed();
        Assert.Equal("0.2.0", _store.Load().DismissedVersion);

        _time.Advance(TimeSpan.FromHours(25));
        Coordinator.OnUnlock();
        _host.FireTimer();
        await Settle(() => _host.Finished == 2);

        Assert.Single(_host.Balloons); // повторного уведомления нет
        Assert.Equal(new Version(0, 2, 0), Coordinator.Pending!.Version); // но пункт меню остаётся
    }

    [Fact]
    public async Task NewerVersionAfterDismissal_NotifiesAgain()
    {
        _store.Save(new UpdateState { DismissedVersion = "0.2.0" });
        ServeRelease("0.3.0");

        StartAndFire();
        await Settle(() => _host.Balloons.Count == 1);

        Assert.Contains("0.3.0", _host.Balloons[0]);
    }

    [Fact]
    public async Task SameVersionFoundTwiceWhileBalloonIsUp_ShowsItOnce()
    {
        ServeRelease();
        StartAndFire();
        await Settle(() => _host.Finished == 1);

        _time.Advance(TimeSpan.FromHours(25));
        Coordinator.OnUnlock();
        _host.FireTimer();
        await Settle(() => _host.Finished == 2);

        Assert.Single(_host.Balloons);
    }

    [Fact]
    public void BalloonClosed_WithoutBalloon_ChangesNothing()
    {
        Coordinator.OnBalloonClosed();

        Assert.Null(_store.Load().DismissedVersion);
    }

    // ---- проверка по просьбе окна настроек ----

    [Fact]
    public async Task CheckNow_IgnoresDailyGateAndSetting_AndDoesNotPopBalloon()
    {
        _autoEnabled = false;
        _store.Save(new UpdateState { LastCheckUtc = _time.GetUtcNow() });
        ServeRelease();

        await Coordinator.CheckNowAsync();

        Assert.Equal(new Version(0, 2, 0), Coordinator.Pending!.Version);
        Assert.Empty(_host.Balloons);
        Assert.Equal(1, _handler.Count(UpdateChecker.LatestReleaseUrl));
    }

    // ---- установка ----

    [Fact]
    public async Task BalloonClick_DownloadsVerifiesLaunchesPerUserSetup_AndQuits()
    {
        ServeRelease();
        StartAndFire();
        await Settle(() => _host.Balloons.Count == 1);

        await Coordinator.OnBalloonClickedAsync();

        var launch = Assert.Single(_host.Launches);
        Assert.Equal(Path.Combine(_temp.Path, "SaySo-Update", "SaySo-Setup-0.2.0.exe"), launch.Path);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /CURRENTUSER", launch.Arguments);
        Assert.False(launch.Elevate);
        Assert.Equal(_setup, _host.LaunchedContent);
        Assert.Equal(1, _host.Quits);
        Assert.Contains(_host.Messages, m => m.Text == OverlayMessages.UpdateDownloading && !m.IsError);
        Assert.Equal(1, _handler.Count(SetupUrl("0.2.0")));
    }

    [Fact]
    public async Task BalloonClick_ForgetsBalloon_SoItIsNotDismissedAfterwards()
    {
        ServeRelease();
        StartAndFire();
        await Settle(() => _host.Balloons.Count == 1);

        await Coordinator.OnBalloonClickedAsync();
        Coordinator.OnBalloonClosed(); // после щелчка Windows может прислать закрытие

        Assert.Null(_store.Load().DismissedVersion);
    }

    [Fact]
    public async Task InstallUnderProgramFiles_IsAllUsers_WithElevation()
    {
        var programFiles = Path.Combine(_temp.Path, "Program Files");
        _exePath = MakeInstall(Path.Combine(programFiles, "SaySo"));
        _programFiles = [programFiles];
        ServeRelease();
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();

        var launch = Assert.Single(_host.Launches);
        Assert.True(launch.Elevate);
        Assert.EndsWith("/UPDATE /ALLUSERS", launch.Arguments);
        Assert.Equal(1, _host.Quits);
    }

    [Fact]
    public async Task UacCancelled_ShowsMessage_KeepsRunning_AndRemovesTheDownload()
    {
        var programFiles = Path.Combine(_temp.Path, "Program Files");
        _exePath = MakeInstall(Path.Combine(programFiles, "SaySo"));
        _programFiles = [programFiles];
        _host.LaunchResult = InstallerLaunchResult.Cancelled;
        ServeRelease();
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();

        Assert.Equal(0, _host.Quits);
        Assert.Contains(_host.Messages, m => m.Text == "Обновление отменено" && !m.IsError);
        Assert.Empty(Directory.GetFiles(Path.Combine(_temp.Path, "SaySo-Update")));
        Assert.NotNull(Coordinator.Pending); // пункт меню остаётся: можно попробовать снова

        _host.LaunchResult = InstallerLaunchResult.Started;
        await Coordinator.InstallAsync();
        Assert.Equal(1, _host.Quits);
    }

    [Fact]
    public async Task LaunchFailure_ShowsError_AndDoesNotQuit()
    {
        _host.LaunchResult = InstallerLaunchResult.Failed;
        ServeRelease();
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();

        Assert.Equal(0, _host.Quits);
        Assert.Contains(_host.Messages, m => m.Text == OverlayMessages.UpdateLaunchFailed && m.IsError);
    }

    [Fact]
    public async Task HashMismatch_NothingIsLaunched_FileRemoved_ErrorShown()
    {
        ServeRelease();
        _handler.Text(ShaUrl("0.2.0"), $"{new string('0', 64)}  SaySo-Setup-0.2.0.exe");
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();

        Assert.Empty(_host.Launches);
        Assert.Equal(0, _host.Quits);
        Assert.Contains(_host.Messages, m => m.Text == "Файл обновления не прошёл проверку" && m.IsError);
        Assert.Empty(Directory.GetFiles(Path.Combine(_temp.Path, "SaySo-Update")));
    }

    [Fact]
    public async Task SizeMismatch_NothingIsLaunched()
    {
        ServeRelease();
        _handler.Bytes(SetupUrl("0.2.0"), new byte[_setup.Length - 1]);
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();

        Assert.Empty(_host.Launches);
        Assert.Contains(_host.Messages, m => m.IsError);
    }

    [Fact]
    public async Task DownloadNetworkError_ShowsMessage_NothingLaunched()
    {
        ServeRelease();
        _handler.Throw(SetupUrl("0.2.0"), new HttpRequestException("обрыв"));
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();

        Assert.Empty(_host.Launches);
        Assert.Contains(_host.Messages, m => m.Text == "Не удалось скачать обновление" && m.IsError);
    }

    [Fact]
    public async Task NotAnInstalledCopy_OpensReleasePage_WithoutDownloading()
    {
        _exePath = Path.Combine(_temp.Path, "Portable", "SaySo.exe"); // рядом нет unins000.exe
        ServeRelease();
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();

        Assert.Equal("https://github.com/Denmurzik/SaySo/releases/tag/v0.2.0", Assert.Single(_host.OpenedUrls));
        Assert.Empty(_host.Launches);
        Assert.Equal(0, _handler.Count(SetupUrl("0.2.0")));
        Assert.Equal(0, _host.Quits);
    }

    [Fact]
    public async Task ReleaseWithoutSetupAsset_OpensReleasePage()
    {
        ServeRelease(installable: false);
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();

        Assert.Single(_host.OpenedUrls);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task InstallWithoutKnownUpdate_ChecksFirst_ThenInstalls()
    {
        ServeRelease();

        await Coordinator.InstallAsync(); // просьба окна настроек: демон ещё не проверял

        Assert.Equal(1, _handler.Count(UpdateChecker.LatestReleaseUrl));
        Assert.Single(_host.Launches);
        Assert.Equal(1, _host.Quits);
    }

    [Fact]
    public async Task InstallWithoutKnownUpdate_WhenUpToDate_SaysSo()
    {
        _handler.Json(UpdateChecker.LatestReleaseUrl, ReleaseJson("v0.1.0", "0.1.0"));

        await Coordinator.InstallAsync();

        Assert.Contains(_host.Messages, m => m.Text == OverlayMessages.UpdateUpToDate && !m.IsError);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task InstallWithoutKnownUpdate_WhenCheckFails_ShowsError()
    {
        _handler.Throw(UpdateChecker.LatestReleaseUrl, new HttpRequestException("нет сети"));

        await Coordinator.InstallAsync();

        Assert.Contains(_host.Messages, m => m.Text == OverlayMessages.UpdateCheckFailed && m.IsError);
        Assert.Empty(_host.Launches);
    }

    [Fact]
    public async Task InstallDuringDictation_WaitsForSessionEnd()
    {
        ServeRelease();
        await Coordinator.CheckNowAsync();
        _host.Idle = false;

        await Coordinator.InstallAsync();
        Assert.Equal(0, _handler.Count(SetupUrl("0.2.0"))); // ничего не качаем и не запускаем

        _host.Idle = true;
        Coordinator.OnSessionEnded();
        await Settle(() => _host.Quits == 1);

        Assert.Single(_host.Launches);
    }

    [Fact]
    public async Task DictationStartsDuringDownload_LaunchWaitsForSessionEnd()
    {
        ServeRelease();
        _handler.Route(SetupUrl("0.2.0"), _ =>
        {
            _host.Idle = false; // диктовка началась, пока качался Setup
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_setup) };
        });
        await Coordinator.CheckNowAsync();

        await Coordinator.InstallAsync();
        Assert.Empty(_host.Launches);
        Assert.Equal(0, _host.Quits);

        await Coordinator.InstallAsync(); // повторная просьба не качает второй раз
        Assert.Equal(1, _handler.Count(SetupUrl("0.2.0")));

        _host.Idle = true;
        Coordinator.OnSessionEnded();

        Assert.Single(_host.Launches);
        Assert.Equal(_setup, _host.LaunchedContent);
        Assert.Equal(1, _host.Quits);
    }

    [Fact]
    public async Task DoubleClick_DoesNotStartTwoDownloads()
    {
        ServeRelease();
        await Coordinator.CheckNowAsync();

        var first = Coordinator.InstallAsync();
        var second = Coordinator.InstallAsync();
        await Task.WhenAll(first, second);

        Assert.Single(_host.Launches);
        Assert.Equal(1, _handler.Count(SetupUrl("0.2.0")));
    }

    [Fact]
    public async Task Dispose_RemovesStagedInstaller_AndSilencesLaterEvents()
    {
        ServeRelease();
        _handler.Route(SetupUrl("0.2.0"), _ =>
        {
            _host.Idle = false;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_setup) };
        });
        await Coordinator.CheckNowAsync();
        await Coordinator.InstallAsync(); // Setup скачан и ждёт конца диктовки

        Coordinator.Dispose();
        _host.Idle = true;
        Coordinator.OnSessionEnded();
        Coordinator.OnStart();

        Assert.Empty(_host.Launches);
        Assert.Empty(_host.Timers);
        Assert.Empty(Directory.GetFiles(Path.Combine(_temp.Path, "SaySo-Update")));
    }
}
