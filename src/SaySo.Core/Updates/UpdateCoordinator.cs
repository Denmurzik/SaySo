using System.ComponentModel;
using SaySo.Core.Diagnostics;
using SaySo.Core.Overlay;

namespace SaySo.Core.Updates;

/// <summary>Всё, что координатору обновлений нужно от процесса (демона или окна настроек); в тестах подменяется.</summary>
public interface IUpdateHost
{
    /// <summary>Диктовка не идёт: проверять и ставить обновление можно только тогда.</summary>
    bool IsDictationIdle { get; }

    /// <summary>Одноразовый таймер окна. Может бросить <see cref="Win32Exception"/>, если таймер не поставился.</summary>
    IDisposable ScheduleOnce(TimeSpan delay, Action action);

    /// <summary>Всплывающее уведомление трея «Доступна версия X.Y.Z — нажмите, чтобы обновить».</summary>
    void ShowBalloon(string text);

    /// <summary>Короткое сообщение пользователю (плашка оверлея / строка состояния).</summary>
    void ShowMessage(string text, bool isError);

    /// <summary>Известная доступная версия изменилась (null — обновлений нет): обновить пункт меню трея.</summary>
    void PendingChanged(ReleaseInfo? release);

    void OpenUrl(string url);

    InstallerLaunchResult LaunchInstaller(string path, string arguments, bool elevate);

    /// <summary>Штатное завершение процесса (путь Quit): после него Setup заменит файлы и перезапустит SaySo.</summary>
    void Quit();

    /// <summary>Операция (проверка, скачивание) закончилась: сбросить память после сетевой активности.</summary>
    void OperationFinished();
}

/// <param name="ExePath">Путь к запущенному SaySo.exe.</param>
/// <param name="ProgramFilesRoots">Каталоги Program Files.</param>
/// <param name="DownloadDirectory">Куда скачивать Setup (<c>%TEMP%\SaySo-Update</c>).</param>
public sealed record UpdateEnvironment(string ExePath, IReadOnlyList<string> ProgramFilesRoots, string DownloadDirectory)
{
    public static UpdateEnvironment Current() => new(
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SaySo.exe"),
        UpdateInstallPlan.SystemProgramFilesRoots(),
        Path.Combine(Path.GetTempPath(), "SaySo-Update"));
}

/// <summary>
/// Порядок обновления в одном месте: когда проверять, о чём сообщать и как ставить. Таймеров в простое нет: проверку
/// запускают события (старт, разблокировка, выход из сна), а не расписание. Диктовка важнее обновления — всё, что
/// требует сети или запуска Setup, ждёт конца сессии. Вызывается с одного потока (главный поток демона).
/// </summary>
public sealed class UpdateCoordinator(
    IUpdateHost host,
    UpdateStateStore store,
    Version current,
    UpdateEnvironment environment,
    Func<bool> autoCheckEnabled,
    Func<HttpClient> httpFactory,
    TimeProvider time) : IDisposable
{
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(60);

    /// <summary>После разблокировки и выхода из сна сети может не быть ещё несколько секунд.</summary>
    public static readonly TimeSpan EventDelay = TimeSpan.FromSeconds(15);

    private readonly CancellationTokenSource _cts = new();
    private readonly UpdateCheckRunner _runner = new(store, current, httpFactory, time);
    private IDisposable? _checkTimer;
    private bool _disposed;
    private bool _checkDeferred;
    private bool _checkNowDeferred;
    private bool _installRequested;
    private bool _installing;
    private Version? _balloonVersion;
    private ReleaseInfo? _balloonDeferred;
    private (StagedInstaller Installer, UpdateInstallPlan Plan, Version Version)? _staged;

    /// <summary>Найденная более новая версия (null — не знаем о такой).</summary>
    public ReleaseInfo? Pending { get; private set; }

    public void OnStart() => ScheduleAutoCheck(StartDelay);

    public void OnResume() => ScheduleAutoCheck(EventDelay);

    public void OnUnlock() => ScheduleAutoCheck(EventDelay);

    /// <summary>
    /// Проверка по просьбе окна настроек: без суточного ограничения и без всплывающего уведомления
    /// (пользователь и так смотрит на результат). Выключенная автопроверка ей не мешает. Идёт диктовка — ждёт её конца.
    /// </summary>
    public Task CheckNowAsync()
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        if (!host.IsDictationIdle)
        {
            _checkNowDeferred = true;
            return Task.CompletedTask;
        }

        return CheckAsync(notify: false);
    }

    /// <summary>Конец диктовочной сессии: выполнить отложенное (уведомление, запуск Setup, установку, проверку).</summary>
    public void OnSessionEnded()
    {
        if (_disposed || !host.IsDictationIdle)
        {
            return;
        }

        if (_balloonDeferred is { } deferred)
        {
            _balloonDeferred = null;
            ShowBalloonIfNeeded(deferred);
        }

        if (_staged is not null)
        {
            LaunchStaged();
        }
        else if (_installRequested)
        {
            _ = ObserveAsync(InstallAsync(), "Отложенная установка обновления");
        }
        else if (_checkNowDeferred)
        {
            _checkNowDeferred = false;
            _ = ObserveAsync(CheckNowAsync(), "Отложенная проверка обновлений");
        }
        else if (_checkDeferred)
        {
            _checkDeferred = false;
            _ = ObserveAsync(AutoCheckAsync(), "Отложенная проверка обновлений");
        }
    }

    /// <summary>Щелчок по всплывающему уведомлению — то же, что пункт меню «Обновить до X.Y.Z».</summary>
    public Task OnBalloonClickedAsync()
    {
        _balloonVersion = null;
        return InstallAsync();
    }

    /// <summary>Уведомление закрыто крестиком или исчезло по таймауту: о этой версии больше не всплываем.</summary>
    public void OnBalloonClosed()
    {
        if (_balloonVersion is not { } version)
        {
            return;
        }

        _balloonVersion = null;
        var state = store.Load();
        state.DismissedVersion = VersionTag.Normalize(version).ToString(3);
        store.Save(state);
    }

    /// <summary>
    /// Установить обновление: показать плашку «Скачиваю…», скачать и проверить Setup, запустить его и завершить процесс.
    /// Идёт диктовка — просьба запоминается и выполняется в <see cref="OnSessionEnded"/>.
    /// </summary>
    public async Task InstallAsync()
    {
        if (_disposed || _installing || _staged is not null)
        {
            return;
        }

        if (!host.IsDictationIdle)
        {
            _installRequested = true;
            Log.Info("Установка обновления отложена до конца диктовки");
            return;
        }

        _installRequested = false;
        _installing = true;
        try
        {
            await InstallCoreAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // процесс завершается
        }
        finally
        {
            _installing = false;
            if (!_disposed)
            {
                host.OperationFinished();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        _checkTimer?.Dispose();
        _checkTimer = null;
        if (_staged is { } staged)
        {
            _staged = null;
            staged.Installer.Delete();
        }

        _cts.Dispose();
    }

    private void ScheduleAutoCheck(TimeSpan delay)
    {
        // Таймер ставится только когда проверка нужна: в остальные дни событие не создаёт ничего.
        if (_disposed || _checkTimer is not null || !IsAutoCheckDue())
        {
            return;
        }

        try
        {
            _checkTimer = host.ScheduleOnce(delay, OnCheckTimer);
        }
        catch (Win32Exception e)
        {
            Log.Error("Таймер проверки обновлений", e); // не критично: проверим по следующему событию
        }
    }

    private void OnCheckTimer()
    {
        _checkTimer?.Dispose(); // таймер одноразовый и уже сработал
        _checkTimer = null;
        _ = ObserveAsync(AutoCheckAsync(), "Проверка обновлений");
    }

    private bool IsAutoCheckDue() => autoCheckEnabled() && UpdatePolicy.IsCheckDue(store.Load(), time.GetUtcNow());

    private async Task AutoCheckAsync()
    {
        // Между постановкой таймера и его срабатыванием настройка могла измениться, а ручная проверка — уже состояться.
        if (_disposed || !IsAutoCheckDue())
        {
            return;
        }

        if (!host.IsDictationIdle)
        {
            _checkDeferred = true;
            return;
        }

        await CheckAsync(notify: true).ConfigureAwait(true);
    }

    private async Task<UpdateCheckResult?> CheckAsync(bool notify)
    {
        UpdateCheckResult result;
        try
        {
            result = await _runner.RunAsync(_cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        if (_disposed)
        {
            return null;
        }

        switch (result.Outcome)
        {
            case UpdateCheckOutcome.Failed:
                Log.Info($"Проверка обновлений не удалась: {result.Failure}");
                break;
            case UpdateCheckOutcome.UpToDate:
                Log.Info($"Проверка обновлений: установлена последняя версия ({VersionTag.Normalize(current).ToString(3)})");
                SetPending(null);
                break;
            default:
                var release = result.Release!;
                Log.Info($"Проверка обновлений: доступна версия {release.Version.ToString(3)} (установленная {VersionTag.Normalize(current).ToString(3)})");
                SetPending(release);
                if (notify)
                {
                    ShowBalloonIfNeeded(release);
                }

                break;
        }

        host.OperationFinished();
        return result;
    }

    private void SetPending(ReleaseInfo? release)
    {
        Pending = release;
        host.PendingChanged(release);
    }

    private void ShowBalloonIfNeeded(ReleaseInfo release)
    {
        if (!UpdatePolicy.ShouldNotify(store.Load(), release.Version))
        {
            return; // пользователь закрыл уведомление об этой версии: остаётся пункт меню и страница «О программе»
        }

        if (!host.IsDictationIdle)
        {
            _balloonDeferred = release; // уведомление посреди диктовки отвлекает
            return;
        }

        _balloonVersion = release.Version;
        host.ShowBalloon(UpdateMessages.Balloon(release.Version));
    }

    private async Task InstallCoreAsync()
    {
        var release = Pending;
        if (release is null)
        {
            // Просьба пришла от окна настроек, а мы ещё не проверяли: перепроверяем и ставим то, что нашли.
            var check = await CheckAsync(notify: false).ConfigureAwait(true);
            if (check is null)
            {
                return;
            }

            if (check.Outcome == UpdateCheckOutcome.Failed)
            {
                host.ShowMessage(OverlayMessages.UpdateCheckFailed, isError: true);
                return;
            }

            if (check.Release is not { } found)
            {
                host.ShowMessage(OverlayMessages.UpdateUpToDate, isError: false);
                return;
            }

            release = found;
        }

        var plan = UpdateInstallPlan.Decide(environment.ExePath, environment.ProgramFilesRoots, File.Exists, release.IsInstallable);
        if (plan.Action == UpdateInstallAction.OpenReleasePage)
        {
            Log.Info($"Версия {release.Version.ToString(3)}: автоматическая установка недоступна — открыта страница релиза");
            host.OpenUrl(release.HtmlUrl);
            return;
        }

        host.ShowMessage(OverlayMessages.UpdateDownloading, isError: false);
        DownloadResult download;
        using (var http = httpFactory())
        {
            download = await new UpdateDownloader(http, environment.DownloadDirectory).DownloadAsync(release, _cts.Token).ConfigureAwait(true);
        }

        if (_disposed)
        {
            download.Installer?.Delete();
            return;
        }

        if (download.Installer is not { } installer)
        {
            Log.Info($"Версия {release.Version.ToString(3)}: файл обновления не получен ({download.Failure})");
            host.ShowMessage(OverlayMessages.ForDownloadFailure(download.Failure!.Value), isError: true);
            return;
        }

        _staged = (installer, plan, release.Version);
        if (host.IsDictationIdle)
        {
            LaunchStaged();
        }
        else
        {
            Log.Info("Запуск обновления отложен до конца диктовки");
        }
    }

    private void LaunchStaged()
    {
        var (installer, plan, version) = _staged!.Value;
        _staged = null;
        InstallerLaunchResult result;
        try
        {
            result = host.LaunchInstaller(installer.Path, plan.Arguments, plan.Elevate);
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            Log.Error("Запуск установщика обновления", e);
            result = InstallerLaunchResult.Failed;
        }

        switch (result)
        {
            case InstallerLaunchResult.Started:
                Log.Info($"Запущена установка версии {version.ToString(3)} ({(plan.AllUsers ? "для всех" : "для текущего пользователя")})");
                installer.Dispose(); // Setup уже держит файл; страж больше не нужен
                host.Quit();
                break;
            case InstallerLaunchResult.Cancelled:
                Log.Info("Обновление отменено в окне UAC");
                installer.Delete();
                host.ShowMessage(OverlayMessages.UpdateCancelled, isError: false);
                break;
            default:
                installer.Delete();
                host.ShowMessage(OverlayMessages.UpdateLaunchFailed, isError: true);
                break;
        }
    }

    // Исключение из async-метода без ожидающего иначе пропало бы незамеченным.
    private static async Task ObserveAsync(Task task, string context)
    {
        try
        {
            await task.ConfigureAwait(true);
        }
        catch (Exception e)
        {
            Log.Error(context, e);
        }
    }
}
