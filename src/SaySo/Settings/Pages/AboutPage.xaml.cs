using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Diagnostics;
using SaySo.Core.Platform;
using SaySo.Core.Storage;
using SaySo.Core.Updates;

namespace SaySo.Settings.Pages;

public sealed partial class AboutPage : UserControl
{
    private static readonly Version CurrentVersion = typeof(AboutPage).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private readonly SettingsModel _model;
    private ReleaseInfo? _release;
    private UpdateInstallPlan? _plan;
    private bool _busy;

    internal AboutPage(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        var version = CurrentVersion.ToString(3);
        VersionText.Text = $"Версия {version} · лицензия Apache-2.0";
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _model.Paths.Root + "\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("AboutPage.OpenDataFolder", ex);
        }
    }

    // Окно настроек проверяет само тем же кодом, что и демон, чтобы показать результат; демону отдельно сообщается о находке
    // (пункт меню трея), а установку он ведёт сам.
    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Проверяю…");
        try
        {
            var runner = new UpdateCheckRunner(new UpdateStateStore(_model.Paths), CurrentVersion, UpdateHttp.Create, TimeProvider.System);
            ShowResult(await runner.RunAsync());
        }
        catch (Exception ex)
        {
            Log.Error("AboutPage.CheckUpdates", ex);
            SetBusy(false, "Не удалось проверить обновления — подробности в журнале");
        }
    }

    private void ShowResult(UpdateCheckResult result)
    {
        _release = null;
        _plan = null;
        UpdateNotes.Visibility = Visibility.Collapsed;
        InstallButton.Visibility = Visibility.Collapsed;
        switch (result.Outcome)
        {
            case UpdateCheckOutcome.Failed:
                SetBusy(false, UpdateMessages.CheckFailed(result.Failure ?? UpdateFailure.BadResponse));
                break;
            case UpdateCheckOutcome.Available:
                var release = result.Release!;
                var env = UpdateEnvironment.Current();
                _release = release;
                _plan = UpdateInstallPlan.Decide(env.ExePath, env.ProgramFilesRoots, File.Exists, release.IsInstallable);
                InstallButton.Content = _plan.Action == UpdateInstallAction.Install ? "Обновить" : "Открыть страницу релиза";
                InstallButton.IsEnabled = true;
                InstallButton.Visibility = Visibility.Visible;
                if (release.Notes.Length > 0)
                {
                    UpdateNotes.Text = release.Notes;
                    UpdateNotes.Visibility = Visibility.Visible;
                }

                IpcClient.Send(IpcProtocol.CheckUpdates); // пункт «Обновить до X.Y.Z» в меню трея
                SetBusy(false, UpdateMessages.Available(release.Version));
                break;
            default:
                SetBusy(false, UpdateMessages.UpToDate);
                break;
        }
    }

    private async void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy || _release is not { } release || _plan is not { } plan)
        {
            return;
        }

        if (plan.Action == UpdateInstallAction.OpenReleasePage)
        {
            UpdateShell.OpenUrl(release.HtmlUrl);
            return;
        }

        InstallButton.IsEnabled = false;
        if (IpcClient.Send(IpcProtocol.InstallUpdate))
        {
            // Демон перепроверит релиз, скачает и проверит Setup, дождётся конца диктовки и перезапустится сам.
            SetBusy(false, "SaySo скачивает обновление и перезапустится — это займёт около минуты");
            return;
        }

        // Демона нет: ставим сами тем же кодом. Setup закроет это окно и перезапустит SaySo.
        SetBusy(true, "Скачиваю обновление…");
        try
        {
            using var coordinator = new UpdateCoordinator(
                new SettingsUpdateHost(ShowInstallMessage, QuitSettings),
                new UpdateStateStore(_model.Paths),
                CurrentVersion,
                UpdateEnvironment.Current(),
                () => false,
                UpdateHttp.Create,
                TimeProvider.System);
            await coordinator.InstallAsync();
        }
        catch (Exception ex)
        {
            Log.Error("AboutPage.InstallUpdate", ex);
            ShowInstallMessage("Не удалось обновить — подробности в журнале", isError: true);
        }
        finally
        {
            SetBusy(false, UpdateStatus.Text);
            InstallButton.IsEnabled = true;
        }
    }

    private void ShowInstallMessage(string text, bool isError) => UpdateStatus.Text = text;

    private void QuitSettings()
    {
        _model.Flush();
        Application.Current.Exit();
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        UpdateStatus.Text = status;
        CheckButton.IsEnabled = !busy;
        UpdateRing.IsActive = busy;
        UpdateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Координатор в окне настроек (демон не запущен): диктовки нет, таймеров и уведомлений тоже.</summary>
internal sealed class SettingsUpdateHost(Action<string, bool> showMessage, Action quit) : IUpdateHost
{
    public bool IsDictationIdle => true;

    public IDisposable ScheduleOnce(TimeSpan delay, Action action) => throw new NotSupportedException("В окне настроек автопроверки нет");

    public void ShowBalloon(string text)
    {
    }

    public void ShowMessage(string text, bool isError) => showMessage(text, isError);

    public void PendingChanged(ReleaseInfo? release)
    {
    }

    public void OpenUrl(string url) => UpdateShell.OpenUrl(url);

    public InstallerLaunchResult LaunchInstaller(string path, string arguments, bool elevate) =>
        UpdateShell.LaunchInstaller(path, arguments, elevate);

    public void Quit() => quit();

    public void OperationFinished()
    {
    }
}
