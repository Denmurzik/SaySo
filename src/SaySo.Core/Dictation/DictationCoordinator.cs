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

    /// <summary>Дольше этого запуск микрофона (Activate/Initialize) считается зависшим драйвером.</summary>
    public static readonly TimeSpan MicrophoneStartTimeout = TimeSpan.FromSeconds(5);

    private const int ErrorTimeoutHResult = unchecked((int)0x800705B4); // HRESULT_FROM_WIN32(ERROR_TIMEOUT)

    private DictationRun? _run;
    private IAudioSource? _source;
    private Task? _starting;
    private IDisposable? _engineTimer;
    private IDisposable? _autoStopTimer;
    private long _pressedAt;
    private PendingError? _pending;
    private bool _cancelledWhileStarting; // Cancel пришёл, пока микрофон ещё запускался: это было сочетание клавиш

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
            if (State == DictationState.Finishing)
            {
                // Нажатие во время обработки прошлой записи не запишется — даём знать звуком ошибки, но так же, как при сбое
                // микрофона, не раньше EngineStartDelay: правый Ctrl+C сразу после диктовки остаётся тихим.
                SchedulePending(null);
            }

            return Task.CompletedTask;
        }

        DiscardPending(endSession: false); // отложенная ошибка прошлой попытки: новое нажатие её отменяет
        _pressedAt = time.GetTimestamp();
        _cancelledWhileStarting = false;
        State = DictationState.Recording;
        _starting = StartCoreAsync();
        return _starting;
    }

    private async Task StartCoreAsync()
    {
        var run = pipeline.Begin();
        _run = run;
        IAudioSource? source = null;
        FailureText? failure = null;
        try
        {
            source = audio(run.PushAudio, e => postToMain(() => OnMicrophoneError(run, e)));
            _source = source;
            await StartSourceAsync(source);
        }
        catch (MicrophoneException e)
        {
            Log.Info($"Микрофон: {e.Kind} (0x{e.NativeError:X8})");
            failure = new FailureText(e.UserMessage, e.Detail);
        }
        catch (Exception e)
        {
            Log.Error("Запуск записи", e);
            failure = FailureText.Same(OverlayMessages.StartFailed);
        }

        if (failure is { } text)
        {
            // Откат: сначала состояние, потом освобождение ресурсов. Сбой уже записан в журнал; микрофон стартует при
            // каждом нажатии горячей клавиши, но пищать на сочетаниях вроде правого Ctrl+C из-за этого нельзя.
            if (ReferenceEquals(run, _run))
            {
                _source = null;
                _run = null;
                State = DictationState.Idle;
                var remaining = EngineStartDelay - time.GetElapsedTime(_pressedAt);
                if (_cancelledWhileStarting)
                {
                    // Сочетание клавиш (Cancel пришёл до сбоя) — ошибку не показываем, даже если сбой медленнее 150 мс
                    // (холодный старт, отказ в доступе, таймаут): только то, чем заканчивается сессия.
                    _cancelledWhileStarting = false;
                    resetHotkey();
                    SessionEnded?.Invoke();
                }
                else if (remaining > TimeSpan.Zero)
                {
                    // Cancel/Stop раньше EngineStartDelay (это было сочетание клавиш) отменят показ. Сброс автомата хука и
                    // SessionEnded — вместе с показом или отменой: после сброса автомат ушёл бы в Idle и не заметил бы вторую клавишу.
                    SchedulePending(text, remaining);
                }
                else
                {
                    Fail(text);
                    resetHotkey();
                    SessionEnded?.Invoke();
                }
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
            if (!auto)
            {
                DiscardPending(endSession: true); // сочетание клавиш: отложенная ошибка (старт микрофона, Finishing) не показывается
            }

            return;
        }

        if (_starting is { IsCompleted: false } starting)
        {
            await starting;
        }

        if (State != DictationState.Recording || !ReferenceEquals(expected, _run))
        {
            if (!auto)
            {
                DiscardPending(endSession: true); // запуск микрофона не удался, пока ждали: Stop отменяет показ ошибки
            }

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
                // Тишина, слишком короткая реплика или пустой ответ движка: не сбой, поэтому тон Info, без звука; история и буфер не трогаются.
                overlay.ShowMessage(OverlayMessages.NoSpeech, OverlayTone.Info);
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
            if (OverlayMessages.ForInsertOutcome(outcome) is { } insertProblem)
            {
                failed = Fail(FailureText.Same(insertProblem));
            }
            else
            {
                PlaySound(SoundKind.Stop);
                // «Только Groq» (ключа Gemini нет) — осознанная настройка: плашка о резерве шумела бы на каждой диктовке.
                if (result.PrimaryError is { } primaryError and not EngineErrorKind.NotConfigured)
                {
                    overlay.ShowMessage(OverlayMessages.FallbackUsed(primaryError), OverlayTone.Info);
                }
                else
                {
                    overlay.Hide();
                }
            }
        }
        catch (DictationFailedException e)
        {
            Log.Info($"Диктовка не удалась: основной={e.PrimaryError}, резерв={e.FallbackError}");
            failed = Fail(new FailureText(e.UserMessage, e.Detail));
        }
        catch (Exception e)
        {
            // Неожиданный сбой (вставка, оверлей, движок): сессия всё равно должна закрыться, а не зависнуть.
            Log.Error("Диктовка", e);
            failed = Fail(FailureText.Same(OverlayMessages.DictationError));
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
            DiscardPending(endSession: true); // сочетание клавиш: отложенная ошибка (старт микрофона, Finishing) не показывается
            return;
        }

        if (_starting is { IsCompleted: false } starting)
        {
            _cancelledWhileStarting = true; // если запуск завершится сбоем, ошибку показывать не нужно (см. StartCoreAsync)
            await starting;
        }

        _cancelledWhileStarting = false;
        if (State != DictationState.Recording || !ReferenceEquals(run, _run))
        {
            DiscardPending(endSession: true); // запуск микрофона не удался, пока ждали: Cancel отменяет показ ошибки
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

    /// <summary>
    /// Запуск микрофона с ограничением по времени: зависший драйвер (Activate/Initialize) не должен навсегда оставить
    /// координатор в Recording. Таймаут — через планировщик, а не <c>Task.WaitAsync</c>: так его можно подменить в тестах.
    /// </summary>
    private async Task StartSourceAsync(IAudioSource source)
    {
        var start = source.StartAsync();
        if (start.IsCompleted)
        {
            await start;
            return;
        }

        var timedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = scheduler.Schedule(MicrophoneStartTimeout, () => timedOut.TrySetResult());
        if (await Task.WhenAny(start, timedOut.Task) == start)
        {
            await start;
            return;
        }

        _ = start.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // позднюю ошибку никто не ждёт
        throw new MicrophoneException(MicrophoneErrorKind.Other, ErrorTimeoutHResult, "Запуск микрофона не завершился вовремя");
    }

    /// <summary>
    /// Ошибка, показ которой отложен на <see cref="EngineStartDelay"/> от нажатия: если за это время пришли Cancel/Stop,
    /// это было сочетание клавиш — ничего не показываем. <paramref name="text"/> == null — только звук ошибки.
    /// </summary>
    private void SchedulePending(FailureText? text, TimeSpan? delay = null)
    {
        if (_pending is not null)
        {
            return;
        }

        var pending = new PendingError(text);
        _pending = pending;
        pending.Timer = scheduler.Schedule(delay ?? EngineStartDelay, () => OnPendingElapsed(pending));
    }

    private void OnPendingElapsed(PendingError pending)
    {
        if (!ReferenceEquals(pending, _pending))
        {
            return;
        }

        _pending = null;
        if (pending.Text is not { } text)
        {
            PlaySound(SoundKind.Error);
            return;
        }

        Fail(text);
        resetHotkey();
        SessionEnded?.Invoke();
    }

    /// <param name="endSession">
    /// Отменённая ошибка сбоя старта завершает «сессию»: сброс автомата хука и <see cref="SessionEnded"/> (отложенная
    /// переустановка хука и перенастройка клавиш). Новое нажатие их не вызывает — оно само начало сессию.
    /// </param>
    private void DiscardPending(bool endSession)
    {
        if (_pending is not { } pending)
        {
            return;
        }

        _pending = null;
        pending.Timer?.Dispose();
        if (endSession && pending.Text is not null)
        {
            resetHotkey();
            SessionEnded?.Invoke();
        }
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

    private bool Fail(FailureText message)
    {
        overlay.ShowMessage(message.Short, OverlayTone.Error);
        PlaySound(SoundKind.Error);
        feedback.SetTray(TrayState.Error, message.Detail);
        return true;
    }

    private void PlaySound(SoundKind kind)
    {
        if (settings().SoundsEnabled)
        {
            feedback.Play(kind);
        }
    }

    private sealed class PendingError(FailureText? text)
    {
        public FailureText? Text { get; } = text;

        public IDisposable? Timer { get; set; }
    }
}
