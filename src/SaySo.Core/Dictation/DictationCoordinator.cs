using SaySo.Core.Audio;
using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Diagnostics;
using SaySo.Core.Engines;
using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Core.Dictation;

/// <summary>Сценарий диктовки поверх портов. Все методы — на главном потоке.</summary>
public sealed class DictationCoordinator(
    DictationPipeline pipeline,
    AudioSourceFactory audio,
    IDictationOverlay overlay,
    ITextOutput output,
    IDictationFeedback feedback,
    IScheduler scheduler,
    HistoryStore history,
    Func<AppSettings> settings,
    Action resetHotkey,
    Action<Action> postToMain,
    TimeProvider time)
{
    public static readonly TimeSpan EngineStartDelay = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan AutoStopAfter = TimeSpan.FromSeconds(AudioFormat.AutoStopSeconds);

    private DictationRun? _run;
    private IAudioSource? _source;
    private Task? _starting;
    private IDisposable? _engineTimer;
    private IDisposable? _autoStopTimer;

    public event Action? SessionEnded;

    public DictationState State { get; private set; }

    public float CurrentLevel => _run?.LastLevel ?? 0f;

    public Task HandleAsync(HotkeyAction action) => action switch
    {
        HotkeyAction.StartRecording => StartAsync(),
        HotkeyAction.StopRecording => StopAsync(_run, auto: false),
        HotkeyAction.Cancel => CancelAsync(),
        _ => Task.CompletedTask,
    };

    private Task StartAsync()
    {
        if (State != DictationState.Idle)
        {
            return Task.CompletedTask;
        }

        State = DictationState.Recording;
        _starting = StartCoreAsync();
        return _starting;
    }

    private async Task StartCoreAsync()
    {
        var run = pipeline.Begin();
        _run = run;
        IAudioSource? source = null;
        string? failure = null;
        try
        {
            source = audio(run.PushAudio, e => postToMain(() => OnMicrophoneError(run, e)));
            _source = source;
            await source.StartAsync();
        }
        catch (MicrophoneException e)
        {
            Log.Info($"Микрофон: {e.Kind} (0x{e.NativeError:X8})");
            failure = e.UserMessage;
        }
        catch (Exception e)
        {
            Log.Error("Запуск записи", e);
            failure = "Не удалось начать запись — подробности в журнале";
        }

        if (failure is not null)
        {
            // Откат: сначала состояние и реакция, потом освобождение ресурсов.
            if (ReferenceEquals(run, _run))
            {
                _source = null;
                _run = null;
                State = DictationState.Idle;
                Fail(failure);
                resetHotkey();
            }

            source?.Dispose();
            await DisposeRunAsync(run);
            return;
        }

        // Реакция (плашка, трей, звук) — вместе со стартом движка: короткие сочетания клавиш остаются тихими.
        if (ReferenceEquals(run, _run))
        {
            _engineTimer = scheduler.Schedule(EngineStartDelay, () => OnEngineStartDelayElapsed(run));
            _autoStopTimer = scheduler.Schedule(AutoStopAfter, () => _ = StopAsync(run, auto: true));
        }
    }

    private void OnEngineStartDelayElapsed(DictationRun run)
    {
        if (State != DictationState.Recording || !ReferenceEquals(run, _run))
        {
            return;
        }

        run.StartPrimary();
        overlay.ShowRecording();
        feedback.SetTray(TrayState.Recording);
        PlaySound(SoundKind.Start);
    }

    private async Task StopAsync(DictationRun? expected, bool auto)
    {
        if (State != DictationState.Recording || expected is null || !ReferenceEquals(expected, _run))
        {
            return;
        }

        if (_starting is { IsCompleted: false } starting)
        {
            await starting;
        }

        if (State != DictationState.Recording || !ReferenceEquals(expected, _run))
        {
            return;
        }

        var run = expected;
        State = DictationState.Finishing;
        var failed = false;
        try
        {
            StopCapture();

            // Слишком короткая запись не тратит запрос к API: сессию движка открываем, только если есть что распознавать.
            if (AudioFormat.BytesToMilliseconds(run.RecordedBytes) >= DictationPipeline.MinSpeechMs)
            {
                run.StartPrimary();
            }

            if (auto)
            {
                PlaySound(SoundKind.Error); // предупреждение: достигнут лимит длины реплики
            }

            overlay.ShowProcessing();
            feedback.SetTray(TrayState.Idle);
            var result = await run.FinishAsync(CancellationToken.None);
            if (result.IsEmpty)
            {
                overlay.Hide();
                return;
            }

            try
            {
                history.Add(new HistoryEntry(time.GetUtcNow(), result.Text, result.Engine));
            }
            catch (Exception e)
            {
                Log.Error("История", e); // сбой записи истории не должен терять продиктованный текст
            }

            var outcome = await output.InsertAsync(result.Text, settings().Hotkey.Keys);
            Log.Info($"Диктовка: движок={result.Engine}, вставка={outcome}, символов={result.Text.Length}");
            switch (outcome)
            {
                case InsertOutcome.Inserted:
                    PlaySound(SoundKind.Stop);
                    if (result.PrimaryError is { } primaryError)
                    {
                        overlay.ShowMessage("Резерв Groq — " + ErrorMessages.Describe(primaryError, "Gemini"), OverlayTone.Info);
                    }
                    else
                    {
                        overlay.Hide();
                    }

                    break;
                case InsertOutcome.BlockedByElevation:
                    failed = Fail("Окно запущено от администратора — текст в буфере обмена, вставьте Ctrl+V");
                    break;
                case InsertOutcome.ClipboardUnavailable:
                    failed = Fail("Буфер обмена занят — текст сохранён в истории");
                    break;
                case InsertOutcome.NoTarget:
                    failed = Fail("Нет активного окна — текст сохранён в истории");
                    break;
                case InsertOutcome.InputBlocked:
                    failed = Fail("Не удалось нажать Ctrl+V — текст в буфере обмена, вставьте вручную");
                    break;
            }
        }
        catch (DictationFailedException e)
        {
            Log.Info($"Диктовка не удалась: основной={e.PrimaryError}, резерв={e.FallbackError}");
            failed = Fail(e.UserMessage);
        }
        catch (Exception e)
        {
            // Неожиданный сбой (вставка, оверлей, движок): сессия всё равно должна закрыться, а не зависнуть.
            Log.Error("Диктовка", e);
            failed = Fail("Ошибка диктовки — подробности в журнале");
            await DisposeRunAsync(run);
        }
        finally
        {
            _run = null;
            State = DictationState.Idle;
            if (!failed)
            {
                feedback.SetTray(TrayState.Idle);
            }

            resetHotkey();
            SessionEnded?.Invoke();
        }
    }

    private async Task CancelAsync()
    {
        if (State != DictationState.Recording || _run is not { } run)
        {
            return;
        }

        if (_starting is { IsCompleted: false } starting)
        {
            await starting;
        }

        if (State != DictationState.Recording || !ReferenceEquals(run, _run))
        {
            return;
        }

        // Сначала освобождаем главный поток для нового нажатия, закрытие сессии движка — после.
        StopCapture();
        _run = null;
        State = DictationState.Idle;
        overlay.Hide();
        feedback.SetTray(TrayState.Idle);
        resetHotkey();
        SessionEnded?.Invoke();
        await DisposeRunAsync(run);
    }

    private void OnMicrophoneError(DictationRun run, MicrophoneException e)
    {
        if (State != DictationState.Recording || !ReferenceEquals(run, _run))
        {
            return; // ошибка прошлой сессии или запись уже завершается
        }

        Log.Info($"Микрофон во время записи: {e.Kind}");
        _ = StopAsync(run, auto: false);
    }

    private static async Task DisposeRunAsync(DictationRun run)
    {
        try
        {
            await run.DisposeAsync();
        }
        catch (Exception e)
        {
            Log.Error("Закрытие реплики", e);
        }
    }

    private void StopCapture()
    {
        _engineTimer?.Dispose();
        _autoStopTimer?.Dispose();
        _engineTimer = null;
        _autoStopTimer = null;
        if (_source is { } source)
        {
            _source = null;
            try
            {
                source.Stop();
            }
            finally
            {
                source.Dispose();
            }
        }
    }

    private bool Fail(string message)
    {
        overlay.ShowMessage(message, OverlayTone.Error);
        PlaySound(SoundKind.Error);
        feedback.SetTray(TrayState.Error);
        return true;
    }

    private void PlaySound(SoundKind kind)
    {
        if (settings().SoundsEnabled)
        {
            feedback.Play(kind);
        }
    }
}
