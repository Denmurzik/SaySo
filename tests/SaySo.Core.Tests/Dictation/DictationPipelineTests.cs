using System.Buffers.Binary;
using SaySo.Core.Audio;
using SaySo.Core.Dictation;
using SaySo.Core.Engines;

namespace SaySo.Core.Tests.Dictation;

public class DictationPipelineTests
{
    private static byte[] Speech(int ms)
    {
        var samples = ms * 16;
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(8000 * Math.Sin(2 * Math.PI * 300 * i / 16000.0)));
        }

        return bytes;
    }

    private static (DictationPipeline Pipeline, FakeStreamingEngine Primary, FakeBatchEngine Fallback) Create(bool withFallback = true)
    {
        var primary = new FakeStreamingEngine();
        var fallback = new FakeBatchEngine();
        return (new DictationPipeline(new FakeEngines { Primary = primary, Fallback = withFallback ? fallback : null }), primary, fallback);
    }

    [Fact]
    public async Task PrimarySuccess_UsesGemini_NoFallback()
    {
        var (p, primary, fallback) = Create();
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        var r = await run.FinishAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DictationResult("текст", "gemini", null), r);
        Assert.Empty(fallback.Calls);
        Assert.True(primary.Sessions.Single().Disposed);
    }

    [Fact]
    public async Task AudioBeforeStartPrimary_IsReplayedInOrder()
    {
        var (p, primary, _) = Create();
        await using var run = p.Begin();
        var audio = Speech(1000);
        run.PushAudio(audio.AsSpan(0, 5000));
        Assert.Empty(primary.Sessions);
        run.StartPrimary();
        run.StartPrimary(); // идемпотентно
        run.PushAudio(audio.AsSpan(5000));
        await run.FinishAsync(TestContext.Current.CancellationToken);

        var session = Assert.Single(primary.Sessions);
        Assert.Equal(audio, session.AllPushed());
        Assert.All(session.Pushed.SkipLast(1), c => Assert.Equal(AudioFormat.ChunkBytes, c.Length));
    }

    [Fact]
    public async Task QuotaAtFinish_SwitchesToNextKey_WithFullAudio_Silently()
    {
        var (p, primary, fallback) = Create();
        primary.Factory = attempt => attempt == 0
            ? Fail.Session(EngineErrorKind.QuotaExceeded)
            : new FakeSession { Complete = () => Task.FromResult("второй ключ") };
        var audio = Speech(1200);
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(audio);
        var r = await run.FinishAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DictationResult("второй ключ", "gemini", null), r);
        Assert.Equal(2, primary.Sessions.Count);
        Assert.Equal(audio, primary.Sessions[1].AllPushed());
        Assert.True(primary.Sessions[0].Disposed);
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public async Task FailedDuringRecording_SwitchesImmediately_AndKeepsStreaming()
    {
        var (p, primary, fallback) = Create();
        var audio = Speech(1000);
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(audio.AsSpan(0, 16000));
        primary.Sessions[0].RaiseFailed(new TranscriptionException(EngineErrorKind.QuotaExceeded, "квота"));

        Assert.Equal(2, primary.Sessions.Count);
        Assert.Equal(audio.AsSpan(0, 16000).ToArray(), primary.Sessions[1].AllPushed());
        Assert.True(primary.Sessions[0].Disposed);

        run.PushAudio(audio.AsSpan(16000));
        var r = await run.FinishAsync(TestContext.Current.CancellationToken);
        Assert.Equal("gemini", r.Engine);
        Assert.Equal(audio, primary.Sessions[1].AllPushed());
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public async Task AllKeysOnPause_FallsBackToGroq_WithFullAudio()
    {
        var (p, primary, fallback) = Create();
        primary.Factory = attempt => attempt == 0
            ? Fail.Session(EngineErrorKind.QuotaExceeded)
            : throw new TranscriptionException(EngineErrorKind.QuotaExceeded, "все ключи на паузе");
        var audio = Speech(1200);
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(audio);
        var r = await run.FinishAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DictationResult("резервный текст", "groq", EngineErrorKind.QuotaExceeded), r);
        var wav = Assert.Single(fallback.Calls);
        Assert.Equal(WavWriter.HeaderBytes + audio.Length, wav.Length);
        Assert.Equal(audio, wav[WavWriter.HeaderBytes..]);
    }

    [Fact]
    public async Task NetworkError_DoesNotSwitchKey_FallsBack()
    {
        var (p, primary, fallback) = Create();
        primary.Factory = _ => Fail.Session(EngineErrorKind.Network);
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        var r = await run.FinishAsync(TestContext.Current.CancellationToken);

        Assert.Single(primary.Sessions);
        Assert.Equal(new DictationResult("резервный текст", "groq", EngineErrorKind.Network), r);
    }

    [Fact]
    public async Task EndlessQuota_StopsAfterMaxAttempts()
    {
        var (p, primary, fallback) = Create();
        primary.Factory = _ => Fail.Session(EngineErrorKind.QuotaExceeded);
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        var r = await run.FinishAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DictationRun.MaxPrimaryAttempts, primary.Sessions.Count);
        Assert.Equal("groq", r.Engine);
    }

    [Fact]
    public async Task Silence_ReturnsEmpty_AndCallsNoEngine()
    {
        var (p, primary, fallback) = Create();
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(new byte[32000]);
        var r = await run.FinishAsync(TestContext.Current.CancellationToken);

        Assert.True(r.IsEmpty);
        Assert.Empty(fallback.Calls);
        Assert.True(primary.Sessions.Single().Disposed);
    }

    [Fact]
    public async Task TooShort_ReturnsEmpty()
    {
        var (p, _, fallback) = Create();
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(200));
        Assert.True((await run.FinishAsync(TestContext.Current.CancellationToken)).IsEmpty);
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public async Task PrimaryEmptyText_IsSuccess_NoFallback()
    {
        var (p, primary, fallback) = Create();
        primary.Factory = _ => new FakeSession { Complete = () => Task.FromResult("") };
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        Assert.True((await run.FinishAsync(TestContext.Current.CancellationToken)).IsEmpty);
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public async Task PrimaryNotConfigured_UsesFallback()
    {
        var (p, primary, _) = Create();
        primary.Factory = _ => throw new TranscriptionException(EngineErrorKind.NotConfigured, "нет ключа");
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        Assert.Equal(new DictationResult("резервный текст", "groq", EngineErrorKind.NotConfigured), await run.FinishAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PrimaryFails_NoFallback_ThrowsWithUserMessage()
    {
        var (p, primary, _) = Create(withFallback: false);
        primary.Factory = _ => Fail.Session(EngineErrorKind.AuthFailed);
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        var ex = await Assert.ThrowsAsync<DictationFailedException>(() => run.FinishAsync(TestContext.Current.CancellationToken));
        Assert.Equal(EngineErrorKind.AuthFailed, ex.PrimaryError);
        Assert.Contains("Gemini", ex.UserMessage);
        Assert.EndsWith("резерв не настроен", ex.UserMessage); // подсказка, что резерв помог бы
        Assert.EndsWith("резерв не настроен", ex.Detail);
    }

    [Fact]
    public async Task NoGeminiKey_NoFallback_JustSaysNoKey()
    {
        var (p, primary, _) = Create(withFallback: false);
        primary.Factory = _ => throw new TranscriptionException(EngineErrorKind.NotConfigured, "нет ключа");
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        var ex = await Assert.ThrowsAsync<DictationFailedException>(() => run.FinishAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Gemini: не задан ключ API", ex.UserMessage);
    }

    [Fact]
    public async Task GroqOnly_GroqFails_DoesNotMentionGemini()
    {
        var (p, primary, fallback) = Create();
        primary.Factory = _ => throw new TranscriptionException(EngineErrorKind.NotConfigured, "нет ключа");
        fallback.Handler = _ => Fail.With(EngineErrorKind.Network);
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        var ex = await Assert.ThrowsAsync<DictationFailedException>(() => run.FinishAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Groq: нет связи с сервером", ex.UserMessage);
        Assert.DoesNotContain("Gemini", ex.Detail);
    }

    [Fact]
    public async Task BothFail_ThrowsWithBothMessages()
    {
        var (p, primary, fallback) = Create();
        primary.Factory = _ => Fail.Session(EngineErrorKind.Network);
        fallback.Handler = _ => Fail.With(EngineErrorKind.AuthFailed);
        await using var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        var ex = await Assert.ThrowsAsync<DictationFailedException>(() => run.FinishAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Gemini и Groq недоступны, подробности в трее", ex.UserMessage);
        Assert.Equal("Gemini: нет связи с сервером; Groq: ключ API недействителен", ex.Detail); // полный текст — для подсказки трея
        Assert.Equal(EngineErrorKind.AuthFailed, ex.FallbackError);
    }

    [Fact]
    public async Task Dispose_WithoutFinish_DisposesSession_NoFallback()
    {
        var (p, primary, fallback) = Create();
        var run = p.Begin();
        run.StartPrimary();
        run.PushAudio(Speech(1000));
        await run.DisposeAsync();
        Assert.True(primary.Sessions.Single().Disposed);
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public async Task LastLevel_And_RecordedBytes_AreTracked()
    {
        var (p, _, _) = Create();
        await using var run = p.Begin();
        run.PushAudio(Speech(500));
        Assert.Equal(16000, run.RecordedBytes);
        Assert.True(run.LastLevel > 0.5f);
    }
}
