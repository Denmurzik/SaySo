using SaySo.Core.Audio;
using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Diagnostics;
using SaySo.Core.Dictation;
using SaySo.Core.Input;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Updates;
using SaySo.Mac.Interop;
using SaySo.Mac.Overlay;
using SaySo.Mac.Platform;

namespace SaySo.Mac.Daemon;

/// <summary>
/// Микрофон сессии. Координатор ловит <see cref="MicrophoneException"/> отдельно от прочих сбоев, поэтому любой сбой
/// запуска оборачивается в неё; <see cref="Stop"/> и <see cref="Dispose"/> не бросают (их зовут при откате и отмене).
/// </summary>
internal sealed class MacAudioSource(string? deviceId, string? deviceName, Action<ReadOnlySpan<byte>> onData, Action<MicrophoneException> onError) : IAudioSource
{
    private readonly AudioCapture _capture = new(deviceId, onData, onError, deviceName);

    public async Task StartAsync()
    {
        try
        {
            await _capture.StartAsync();
        }
        catch (MicrophoneException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new MicrophoneException(MicrophoneErrorKind.Other, e.HResult, $"Запуск захвата: {e.GetType().Name}");
        }
    }

    public void Stop()
    {
        try
        {
            _capture.Stop();
        }
        catch (Exception e)
        {
            Log.Error("Остановка захвата", e);
        }
    }

    public void Dispose()
    {
        try
        {
            _capture.Dispose();
        }
        catch (Exception e)
        {
            Log.Error("Освобождение захвата", e);
        }
    }
}

/// <param name="onInsert">Вызывается перед вставкой: текст получен и сохранён в истории — сессия состоялась.</param>
internal sealed class InserterOutput(MacTextInserter inserter, Action onInsert) : ITextOutput
{
    public Task<InsertOutcome> InsertAsync(string text, IReadOnlyCollection<int> keysToRelease)
    {
        onInsert();
        return inserter.InsertAsync(text, keysToRelease);
    }
}

internal sealed class DaemonFeedback(Action<TrayState, string?> setTray, Func<string> soundTheme) : IDictationFeedback
{
    public void Play(SoundKind kind) => MacSoundPlayer.Play(kind, soundTheme());

    public void SetTray(TrayState state, string? detail = null) => setTray(state, detail);
}

/// <summary>Таймеры координатора на главной очереди; отменённый таймер не срабатывает никогда.</summary>
internal sealed class MainQueueScheduler : IScheduler
{
    public IDisposable Schedule(TimeSpan delay, Action action) => Dispatch.After(delay, action);
}

/// <summary>
/// Связь координатора обновлений с демоном. Установщика на macOS нет: найденная версия — это плашка «Доступна версия
/// X.Y.Z» (один раз на версию, как всплывающее уведомление трея) и пункт меню со страницей релиза.
/// </summary>
internal sealed class MacUpdateHost(OverlayPanel overlay, Func<DictationState> dictationState, Func<UpdateCoordinator?> coordinator, Action finished) : IUpdateHost
{
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(3);

    public bool IsDictationIdle => dictationState() == DictationState.Idle;

    public IDisposable ScheduleOnce(TimeSpan delay, Action action) => Dispatch.After(delay, action);

    // Щёлкнуть плашку нельзя (она пропускает щелчки), поэтому показ считается закрытым уведомлением: об этой версии
    // плашка больше не напомнит, а пункт меню «Страница релиза X.Y.Z» остаётся.
    public void ShowBalloon(string text)
    {
        if (coordinator()?.Pending is not { } release || !IsDictationIdle)
        {
            return;
        }

        overlay.ShowMessage(UpdateMessages.Available(release.Version), OverlayTone.Info);
        Dispatch.After(NoticeDuration, () => coordinator()?.OnBalloonClosed());
    }

    public void ShowMessage(string text, bool isError)
    {
        if (IsDictationIdle)
        {
            overlay.ShowMessage(text, isError ? OverlayTone.Error : OverlayTone.Info);
        }
    }

    public void PendingChanged(ReleaseInfo? release)
    {
    }

    public void OpenUrl(string url) => UpdateShell.OpenUrl(url);

    public Task<InstallerRun> RunInstallerAsync(string path, string arguments, CancellationToken ct) =>
        Task.FromResult(new InstallerRun(InstallerLaunchResult.Failed)); // на macOS план всегда «страница релиза»

    public void OperationFinished() => finished();
}
