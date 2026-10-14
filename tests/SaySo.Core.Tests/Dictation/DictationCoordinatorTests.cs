using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Dictation;
using SaySo.Core.Engines;
using SaySo.Core.Hotkeys;
using SaySo.Core.Input;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Core.Tests.Dictation;

public sealed class DictationCoordinatorTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeStreamingEngine _primary = new();
    private readonly FakeBatchEngine _fallback = new();
    private readonly FakeOverlay _overlay = new();
    private readonly FakeOutput _output = new();
    private readonly FakeFeedback _feedback = new();
    private readonly ManualScheduler _scheduler = new();
    private readonly AppSettings _settings = new();
    private readonly List<FakeAudioSource> _sources = [];
    private readonly HistoryStore _history;
    private int _resets;

    public DictationCoordinatorTests()
    {
        _history = new HistoryStore(new AppPaths(_dir.Path));
    }

    public void Dispose() => _dir.Dispose();

    private DictationCoordinator Create(bool withFallback = true)
    {
        var pipeline = new DictationPipeline(
            new FakeEngines { Primary = _primary, Fallback = withFallback ? _fallback : null });
        return new DictationCoordinator(
            pipeline,
            (onData, onError) =>
            {
                var s = new FakeAudioSource(onData, onError);
                _sources.Add(s);
                return s;
            },
            _overlay, _output, _feedback, _scheduler, _history, () => _settings,
            () => _resets++, action => action(), TimeProvider.System);
    }

    [Fact]
    public async Task StartSpeakStop_InsertsText_RecordsHistory_AndResetsHotkey()
    {
        var c = Create();
        var ended = 0;
        c.SessionEnded += () => ended++;

        await c.HandleAsync(HotkeyAction.StartRecording);
        Assert.Equal(DictationState.Recording, c.State);
        Assert.Empty(_overlay.Calls); // реакция — только через 150 мс
        Assert.Empty(_feedback.Sounds);
        Assert.Contains(_scheduler.Items, i => i.Delay == DictationCoordinator.EngineStartDelay);
        Assert.Contains(_scheduler.Items, i => i.Delay == DictationCoordinator.AutoStopAfter);

        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        Assert.Single(_primary.Sessions);
        Assert.Equal(["recording"], _overlay.Calls);
        Assert.Equal([TrayState.Recording], _feedback.Tray);
        Assert.Equal([SoundKind.Start], _feedback.Sounds);
        _sources[0].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal(["текст"], _output.Texts);
        Assert.Equal("текст", _history.Load().Single().Text);
        Assert.Equal("gemini", _history.Load().Single().Engine);
        Assert.Equal(["recording", "processing", "hide"], _overlay.Calls);
        Assert.Equal([SoundKind.Start, SoundKind.Stop], _feedback.Sounds);
        Assert.Equal(TrayState.Idle, _feedback.Tray[^1]);
        Assert.True(_sources[0].Stopped);
        Assert.Equal(1, _resets);
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task Cancel_InsertsNothing_AndDisposesEngineSession()
    {
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(800));
        await c.HandleAsync(HotkeyAction.Cancel);

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Empty(_output.Texts);
        Assert.Empty(_history.Load());
        Assert.Equal("hide", _overlay.Calls[^1]);
        Assert.True(_primary.Sessions.Single().Disposed);
        Assert.True(_sources[0].Stopped);
        Assert.Equal(1, _resets);
        Assert.All(_scheduler.Items, i => Assert.True(i.Cancelled));
    }

    [Fact]
    public async Task ShortcutCancelledBeforeFeedbackDelay_IsSilent_AndUsesNoEngine()
    {
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _sources[0].Push(TestAudio.Speech(100));
        await c.HandleAsync(HotkeyAction.Cancel); // правый Ctrl + C: отмена раньше 150 мс

        Assert.Empty(_feedback.Sounds);
        Assert.DoesNotContain("recording", _overlay.Calls);
        Assert.DoesNotContain(TrayState.Recording, _feedback.Tray);
        Assert.Empty(_primary.Sessions);
        Assert.Equal(DictationState.Idle, c.State);

        _scheduler.Fire(DictationCoordinator.EngineStartDelay); // запоздавший таймер ничего не делает
        Assert.Empty(_primary.Sessions);
        Assert.Empty(_feedback.Sounds);
    }

    [Fact]
    public async Task MicrophoneDenied_ShowsPrivacyHint_AndReturnsToIdle()
    {
        var denied = MicrophoneException.FromHResult(unchecked((int)0x80070005), "Initialize");
        var pipeline = new DictationPipeline(new FakeEngines { Primary = _primary });
        var coordinator = new DictationCoordinator(pipeline,
            (d, e) => new FakeAudioSource(d, e) { StartResult = Task.FromException(denied) },
            _overlay, _output, _feedback, _scheduler, _history, () => _settings, () => _resets++, a => a(), TimeProvider.System);

        await coordinator.HandleAsync(HotkeyAction.StartRecording);

        Assert.Equal(DictationState.Idle, coordinator.State);
        Assert.StartsWith("message:Error:", _overlay.Calls.Single());
        Assert.Contains("Параметры", _overlay.Calls.Single());
        Assert.Contains("Конфиденциальность", _feedback.TrayDetails[^1]); // полный путь — в подсказке значка
        Assert.Equal([SoundKind.Error], _feedback.Sounds);
        Assert.Equal(TrayState.Error, _feedback.Tray[^1]);
        Assert.Equal(1, _resets);
        Assert.Empty(_primary.Sessions);
    }

    [Fact]
    public async Task Silence_InsertsNothing_HidesOverlay()
    {
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _sources[0].Push(new byte[32000]);
        await c.HandleAsync(HotkeyAction.StopRecording);

        Assert.Empty(_output.Texts);
        Assert.Empty(_history.Load());
        Assert.Equal("hide", _overlay.Calls[^1]);
        Assert.DoesNotContain(SoundKind.Error, _feedback.Sounds);
    }

    [Fact]
    public async Task FallbackUsed_ShowsInfoAboutGroq()
    {
        // Единственный ключ исчерпал квоту, других нет — уходим в Groq.
        _primary.Factory = attempt => attempt == 0
            ? Fail.Session(EngineErrorKind.QuotaExceeded)
            : throw new TranscriptionException(EngineErrorKind.QuotaExceeded, "все ключи на паузе");
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);

        Assert.Equal(["резервный текст"], _output.Texts);
        Assert.Equal("groq", _history.Load().Single().Engine);
        Assert.StartsWith("message:Info:Резерв Groq", _overlay.Calls[^1]);
        Assert.Contains("лимит", _overlay.Calls[^1]);
    }

    [Fact]
    public async Task GroqOnlyMode_FallbackText_ShowsNoReservePlaque()
    {
        // Ключа Gemini нет — осознанная настройка «только Groq»: после каждой диктовки плашку не показываем.
        _primary.Factory = _ => throw new TranscriptionException(EngineErrorKind.NotConfigured, "нет ключа");
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);

        Assert.Equal(["резервный текст"], _output.Texts);
        Assert.DoesNotContain(_overlay.Calls, x => x.StartsWith("message:", StringComparison.Ordinal));
        Assert.Equal("hide", _overlay.Calls[^1]);
        Assert.Equal(SoundKind.Stop, _feedback.Sounds[^1]);
    }

    [Fact]
    public async Task BothEnginesFail_ShortTextOnOverlay_FullTextInTrayDetail()
    {
        _primary.Factory = attempt => attempt == 0
            ? Fail.Session(EngineErrorKind.Network)
            : throw new TranscriptionException(EngineErrorKind.Network, "все ключи на паузе");
        _fallback.Handler = _ => Fail.With(EngineErrorKind.RegionBlocked);
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);

        Assert.Equal("message:Error:Gemini и Groq недоступны — подробности в трее", _overlay.Calls[^1]);
        Assert.Equal(TrayState.Error, _feedback.Tray[^1]);
        Assert.Equal("Gemini: нет связи с сервером; Groq: недоступен в вашем регионе — проверьте VPN", _feedback.TrayDetails[^1]);
    }

    [Fact]
    public async Task BothEnginesFail_ShowsError()
    {
        _primary.Factory = attempt => attempt == 0
            ? Fail.Session(EngineErrorKind.AuthFailed)
            : throw new TranscriptionException(EngineErrorKind.AuthFailed, "все ключи на паузе");
        var c = Create(withFallback: false);
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);

        Assert.Empty(_output.Texts);
        Assert.StartsWith("message:Error:Gemini", _overlay.Calls[^1]);
        Assert.Equal(SoundKind.Error, _feedback.Sounds[^1]);
        Assert.Equal(TrayState.Error, _feedback.Tray[^1]);
        Assert.Equal(DictationState.Idle, c.State);
    }

    [Theory]
    [InlineData(InsertOutcome.BlockedByElevation, "Админ-окно")]
    [InlineData(InsertOutcome.ClipboardUnavailable, "Буфер обмена занят")]
    [InlineData(InsertOutcome.NoTarget, "Нет активного окна")]
    [InlineData(InsertOutcome.InputBlocked, "Не вставилось")]
    public async Task InsertProblems_AreExplained_TextStaysInHistory(InsertOutcome outcome, string fragment)
    {
        _output.Outcome = outcome;
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);

        Assert.Single(_history.Load());
        Assert.StartsWith("message:Error:", _overlay.Calls[^1]);
        Assert.Contains(fragment, _overlay.Calls[^1]);
    }

    [Fact]
    public async Task StartWhileFinishing_IsIgnored()
    {
        _output.Gate = new TaskCompletionSource();
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        var stopping = c.HandleAsync(HotkeyAction.StopRecording);
        await WaitUntilAsync(() => _output.Texts.Count > 0);

        Assert.Equal(DictationState.Finishing, c.State);
        await c.HandleAsync(HotkeyAction.StartRecording);
        Assert.Single(_sources);

        _output.Gate.SetResult();
        await stopping;
        Assert.Equal(DictationState.Idle, c.State);
    }

    [Fact]
    public async Task AutoStop_FinishesWithWarningSound()
    {
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        _scheduler.Fire(DictationCoordinator.AutoStopAfter);
        await WaitUntilAsync(() => c.State == DictationState.Idle);

        Assert.Equal(["текст"], _output.Texts);
        Assert.Equal([SoundKind.Start, SoundKind.Error, SoundKind.Stop], _feedback.Sounds);
    }

    [Fact]
    public async Task StopDuringSlowMicrophoneStart_WaitsForStart()
    {
        var micReady = new TaskCompletionSource();
        var pipeline = new DictationPipeline(new FakeEngines { Primary = _primary });
        FakeAudioSource? source = null;
        var c = new DictationCoordinator(pipeline,
            (d, e) => source = new FakeAudioSource(d, e) { StartResult = micReady.Task },
            _overlay, _output, _feedback, _scheduler, _history, () => _settings, () => _resets++, a => a(), TimeProvider.System);

        var starting = c.HandleAsync(HotkeyAction.StartRecording);
        var stopping = c.HandleAsync(HotkeyAction.StopRecording);
        Assert.False(stopping.IsCompleted);
        micReady.SetResult();
        await starting;
        source!.Push(TestAudio.Speech(1000));
        await stopping;
        Assert.True(source.Stopped);
        Assert.Equal(DictationState.Idle, c.State);
    }

    [Fact]
    public async Task MicrophoneLostDuringRecording_TranscribesWhatWasSaid()
    {
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        _sources[0].Fail(MicrophoneException.FromHResult(unchecked((int)0x88890004), "GetBuffer"));
        await WaitUntilAsync(() => c.State == DictationState.Idle);

        Assert.Equal(["текст"], _output.Texts);
    }

    [Fact]
    public async Task SoundsDisabled_PlaysNothing()
    {
        _settings.SoundsEnabled = false;
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);
        Assert.Empty(_feedback.Sounds);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Условие не выполнилось за 5 секунд");
            }

            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    private DictationCoordinator CreateWith(AudioSourceFactory audio, Action? onEnded = null)
    {
        var pipeline = new DictationPipeline(new FakeEngines { Primary = _primary, Fallback = _fallback });
        var c = new DictationCoordinator(pipeline, audio,
            _overlay, _output, _feedback, _scheduler, _history, () => _settings, () => _resets++, a => a(), TimeProvider.System);
        if (onEnded is not null)
        {
            c.SessionEnded += onEnded;
        }

        return c;
    }

    private async Task SpeakAndStopAsync(DictationCoordinator c)
    {
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);
    }

    [Fact]
    public async Task HistoryWriteFails_TextStillInserted_NoErrorShown()
    {
        // history.bin — каталог: запись истории гарантированно падает.
        var bad = new AppPaths(Path.Combine(_dir.Path, "bad"));
        Directory.CreateDirectory(bad.HistoryFile);
        var badHistory = new HistoryStore(bad);
        Assert.ThrowsAny<Exception>(() => badHistory.Add(new HistoryEntry(DateTimeOffset.UtcNow, "x", "gemini")));

        var pipeline = new DictationPipeline(new FakeEngines { Primary = _primary });
        var c = new DictationCoordinator(pipeline,
            (d, e) =>
            {
                var s = new FakeAudioSource(d, e);
                _sources.Add(s);
                return s;
            },
            _overlay, _output, _feedback, _scheduler, badHistory, () => _settings, () => _resets++, a => a(), TimeProvider.System);
        await SpeakAndStopAsync(c);

        Assert.Equal(["текст"], _output.Texts);
        Assert.DoesNotContain(_overlay.Calls, x => x.StartsWith("message:Error", StringComparison.Ordinal));
        Assert.DoesNotContain(SoundKind.Error, _feedback.Sounds);
        Assert.Equal(SoundKind.Stop, _feedback.Sounds[^1]);
        Assert.Equal(DictationState.Idle, c.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertThrows_ShowsError_ReturnsToIdle(bool cancellation)
    {
        _output.Throw = cancellation ? new OperationCanceledException() : new InvalidOperationException("сбой вставки");
        var c = Create();
        var ended = 0;
        c.SessionEnded += () => ended++;
        await SpeakAndStopAsync(c);

        Assert.Equal(DictationState.Idle, c.State);
        Assert.StartsWith("message:Error:Ошибка диктовки", _overlay.Calls[^1]);
        Assert.Equal(SoundKind.Error, _feedback.Sounds[^1]);
        Assert.Equal(TrayState.Error, _feedback.Tray[^1]);
        Assert.Equal(1, _resets);
        Assert.Equal(1, ended);
        Assert.Single(_history.Load());
    }

    [Fact]
    public async Task OverlayThrowsWhileFinishing_DoesNotLeaveFinishingForever()
    {
        _overlay.ThrowOnProcessing = true;
        var c = Create();
        var ended = 0;
        c.SessionEnded += () => ended++;
        await SpeakAndStopAsync(c);

        Assert.Equal(DictationState.Idle, c.State);
        Assert.Equal(1, _resets);
        Assert.Equal(1, ended);
        Assert.True(_sources[0].Stopped);
        Assert.Empty(_output.Texts);
        Assert.Contains("Ошибка диктовки", _overlay.Calls[^1]);
    }

    [Fact]
    public async Task TooShortRecording_OpensNoEngineSession()
    {
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        _sources[0].Push(TestAudio.Speech(100));
        await c.HandleAsync(HotkeyAction.StopRecording);

        Assert.Empty(_primary.Sessions);
        Assert.Empty(_output.Texts);
        Assert.Empty(_history.Load());
        Assert.Equal("hide", _overlay.Calls[^1]);
        Assert.Equal(DictationState.Idle, c.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioStartFailsWithOtherException_RollsBackLikeMicrophoneFailure(bool factoryThrows)
    {
        var boom = new InvalidOperationException("сбой");
        FakeAudioSource? source = null;
        var c = CreateWith((d, e) =>
        {
            if (factoryThrows)
            {
                throw boom;
            }

            return source = new FakeAudioSource(d, e) { StartResult = Task.FromException(boom) };
        });

        await c.HandleAsync(HotkeyAction.StartRecording);

        Assert.Equal(DictationState.Idle, c.State);
        Assert.StartsWith("message:Error:Не удалось начать запись", _overlay.Calls.Single());
        Assert.Equal([SoundKind.Error], _feedback.Sounds);
        Assert.Equal(TrayState.Error, _feedback.Tray[^1]);
        Assert.Equal(1, _resets);
        Assert.Empty(_primary.Sessions);
        if (!factoryThrows)
        {
            Assert.True(source!.Disposed);
        }
    }

    [Fact]
    public async Task StaleCallbacksOfPreviousSession_DoNotAffectNewSession()
    {
        var c = Create();
        await c.HandleAsync(HotkeyAction.StartRecording);
        var oldEngineTimer = _scheduler.Items.Single(i => i.Delay == DictationCoordinator.EngineStartDelay).Action;
        var oldAutoStop = _scheduler.Items.Single(i => i.Delay == DictationCoordinator.AutoStopAfter).Action;
        await c.HandleAsync(HotkeyAction.Cancel);
        await c.HandleAsync(HotkeyAction.StartRecording);
        Assert.Equal(2, _sources.Count);

        oldEngineTimer();
        Assert.Empty(_primary.Sessions);
        Assert.DoesNotContain("recording", _overlay.Calls);
        Assert.Empty(_feedback.Sounds);

        oldAutoStop();
        _sources[0].Fail(MicrophoneException.FromHResult(unchecked((int)0x88890004), "GetBuffer"));
        Assert.Equal(DictationState.Recording, c.State);
        Assert.False(_sources[1].Stopped);

        _sources[1].Push(TestAudio.Speech(1000));
        await c.HandleAsync(HotkeyAction.StopRecording);
        Assert.Equal(["текст"], _output.Texts);
        Assert.Equal(DictationState.Idle, c.State);
    }

    [Fact]
    public async Task DoubleStop_FinishesOnce()
    {
        _output.Gate = new TaskCompletionSource();
        var c = Create();
        var ended = 0;
        c.SessionEnded += () => ended++;
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        var first = c.HandleAsync(HotkeyAction.StopRecording);
        await WaitUntilAsync(() => _output.Texts.Count > 0);
        await c.HandleAsync(HotkeyAction.StopRecording); // во время Finishing

        _output.Gate.SetResult();
        await first;
        await c.HandleAsync(HotkeyAction.StopRecording); // после завершения

        Assert.Equal(["текст"], _output.Texts);
        Assert.Equal(1, _resets);
        Assert.Equal(1, ended);
        Assert.Single(_history.Load());
    }

    [Fact]
    public async Task CancelDuringFinishing_IsIgnored()
    {
        _output.Gate = new TaskCompletionSource();
        var c = Create();
        var ended = 0;
        c.SessionEnded += () => ended++;
        await c.HandleAsync(HotkeyAction.StartRecording);
        _scheduler.Fire(DictationCoordinator.EngineStartDelay);
        _sources[0].Push(TestAudio.Speech(1000));
        var stopping = c.HandleAsync(HotkeyAction.StopRecording);
        await WaitUntilAsync(() => _output.Texts.Count > 0);

        await c.HandleAsync(HotkeyAction.Cancel);
        Assert.Equal(DictationState.Finishing, c.State);
        Assert.Equal(0, _resets);

        _output.Gate.SetResult();
        await stopping;
        Assert.Equal(["recording", "processing", "hide"], _overlay.Calls);
        Assert.Equal(1, _resets);
        Assert.Equal(1, ended);
    }

    [Fact]
    public async Task CancelDuringSlowMicrophoneStart_WaitsThenCancels()
    {
        var micReady = new TaskCompletionSource();
        FakeAudioSource? source = null;
        var ended = 0;
        var c = CreateWith((d, e) => source = new FakeAudioSource(d, e) { StartResult = micReady.Task }, () => ended++);

        var starting = c.HandleAsync(HotkeyAction.StartRecording);
        var cancelling = c.HandleAsync(HotkeyAction.Cancel);
        Assert.False(cancelling.IsCompleted);

        micReady.SetResult();
        await starting;
        await cancelling;

        Assert.Equal(DictationState.Idle, c.State);
        Assert.True(source!.Stopped);
        Assert.Empty(_output.Texts);
        Assert.Empty(_primary.Sessions);
        Assert.Equal(1, _resets);
        Assert.Equal(1, ended);
        Assert.All(_scheduler.Items, i => Assert.True(i.Cancelled));
    }

    [Fact]
    public async Task SecondCancel_DoesNotRepeatResetOrSessionEnded()
    {
        var c = Create();
        var ended = 0;
        c.SessionEnded += () => ended++;
        await c.HandleAsync(HotkeyAction.StartRecording);
        await c.HandleAsync(HotkeyAction.Cancel);
        await c.HandleAsync(HotkeyAction.Cancel);

        Assert.Equal(1, _resets);
        Assert.Equal(1, ended);
    }
}
