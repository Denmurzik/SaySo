using SaySo.Core.Diagnostics;
using SaySo.Core.Dictation;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Updates;

namespace SaySo.Daemon;

/// <summary>Связь координатора обновлений с демоном: окно (таймер), трей (уведомление), оверлей (сообщения), выход.</summary>
/// <param name="finished">Проверка или скачивание закончились: вернуть память, взятую сетью и TLS.</param>
internal sealed class DaemonUpdateHost(
    DaemonWindow window,
    TrayIcon tray,
    OverlayWindow overlay,
    Func<DictationState> dictationState,
    Action finished) : IUpdateHost
{
    public bool IsDictationIdle => dictationState() == DictationState.Idle;

    public IDisposable ScheduleOnce(TimeSpan delay, Action action) => window.SetTimer(delay, action);

    public void ShowBalloon(string text)
    {
        if (!tray.ShowBalloon(UpdateMessages.BalloonTitle, text))
        {
            Log.Info("Уведомление об обновлении не показано: значка в трее нет (остаётся пункт меню)");
        }
    }

    // Плашка — только в простое: посреди диктовки она занята статусом записи.
    public void ShowMessage(string text, bool isError)
    {
        if (IsDictationIdle)
        {
            overlay.ShowMessage(text, isError ? OverlayTone.Error : OverlayTone.Info);
        }
    }

    // Пункт меню читает UpdateCoordinator.Pending в момент показа меню — отдельное состояние не нужно.
    public void PendingChanged(ReleaseInfo? release)
    {
    }

    public void OpenUrl(string url) => UpdateShell.OpenUrl(url);

    // Setup запускается без повышения прав и ждётся асинхронно; остановит демон сам Setup — по IPC Quit (путь RequestQuit).
    public Task<InstallerRun> RunInstallerAsync(string path, string arguments, CancellationToken ct) =>
        UpdateShell.RunInstallerAsync(path, arguments, ct);

    public void OperationFinished() => finished();
}
