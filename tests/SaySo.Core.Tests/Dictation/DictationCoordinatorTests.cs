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
        Assert.Contains("Конфиденциальность", _overlay.Calls.Single());
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
    [InlineData(InsertOutcome.BlockedByElevation, "администратора")]
    [InlineData(InsertOutcome.ClipboardUnavailable, "Буфер обмена занят")]
    [InlineData(InsertOutcome.NoTarget, "Нет активного окна")]
    [InlineData(InsertOutcome.InputBlocked, "Не удалось нажать Ctrl+V")]
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
        while (_output.Texts.Count == 0)
        {
            await Task.Delay(5);
        }

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
        while (c.State != DictationState.Idle)
        {
            await Task.Delay(5);
        }

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
        while (c.State != DictationState.Idle)
        {
            await Task.Delay(5);
        }

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
}
