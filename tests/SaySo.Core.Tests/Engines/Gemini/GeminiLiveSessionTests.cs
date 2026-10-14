using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Settings;

namespace SaySo.Core.Tests.Engines.Gemini;

public class GeminiLiveSessionTests
{
    private static readonly CompletionTimings Fast = new(QuietMs: 150, QuietWithPendingInterimMs: 300, HardTimeoutMs: 1500, SetupTimeoutMs: 500);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    private static GeminiConnection Conn() => new(
        new Uri("wss://example.test/ws/BidiGenerateContent"),
        "TEST-KEY",
        new GeminiSessionOptions("gemini-3.5-transcribe-live", TranscriptionMode.Smart, [], []));

    private static FakeWebSocketTransport AutoSetup()
    {
        var t = new FakeWebSocketTransport();
        t.OnSent = (msg, tr) =>
        {
            if (msg.Contains("\"setup\""))
            {
                tr.ServerSends("""{"setupComplete":{}}""");
            }
        };
        return t;
    }

    [Fact]
    public async Task HappyPath_FinalAndTurnComplete()
    {
        var t = new FakeWebSocketTransport();
        t.OnSent = (msg, tr) =>
        {
            if (msg.Contains("\"setup\""))
            {
                tr.ServerSends("""{"setupComplete":{}}""");
            }

            if (msg.Contains("audioStreamEnd"))
            {
                tr.ServerSends("""{"serverContent":{"inputTranscription":{"text":"Встретимся в субботу."}}}""");
                tr.ServerSends("""{"serverContent":{"turnComplete":true}}""");
            }
        };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        s.Start();
        s.PushAudio(new byte[3200]);

        var text = await s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout);

        Assert.Equal("Встретимся в субботу.", text);
        Assert.Equal(new Uri("wss://example.test/ws/BidiGenerateContent"), t.ConnectedUri);
        Assert.Equal("TEST-KEY", t.Headers!["x-goog-api-key"]);
        Assert.DoesNotContain("TEST-KEY", t.ConnectedUri!.ToString());
    }

    [Fact]
    public async Task AudioIsHeldUntilSetupComplete_ThenSentInOrder()
    {
        var t = new FakeWebSocketTransport();
        await using var s = new GeminiLiveSession(t, Conn(), Fast with { SetupTimeoutMs = 3000 });
        s.Start();
        s.PushAudio(new byte[] { 1, 0 });
        s.PushAudio(new byte[] { 2, 0 });
        s.PushAudio(new byte[] { 3, 0 });
        await Task.Delay(150);
        Assert.Single(t.Sent); // только setup

        t.OnSent = (msg, tr) =>
        {
            if (msg.Contains("audioStreamEnd"))
            {
                tr.ServerSends("""{"serverContent":{"inputTranscription":{"text":"ок"},"turnComplete":true}}""");
            }
        };
        t.ServerSends("""{"setupComplete":{}}""");
        var completion = s.CompleteAsync(CancellationToken.None);
        await completion.WaitAsync(TestTimeout);

        var sent = t.Sent;
        Assert.Contains("\"setup\"", sent[0]);
        Assert.Contains("\"data\":\"AQA=\"", sent[1]);
        Assert.Contains("\"data\":\"AgA=\"", sent[2]);
        Assert.Contains("\"data\":\"AwA=\"", sent[3]);
        Assert.Contains("audioStreamEnd", sent[4]);
    }

    [Fact]
    public async Task NoTurnComplete_FinishesAfterQuietPeriod()
    {
        var t = AutoSetup();
        var baseHandler = t.OnSent!;
        t.OnSent = (msg, tr) =>
        {
            baseHandler(msg, tr);
            if (msg.Contains("audioStreamEnd"))
            {
                tr.ServerSends("""{"serverContent":{"inputTranscription":{"text":"Первое."}}}""");
                tr.ServerSends("""{"serverContent":{"inputTranscription":{"text":"Второе."}}}""");
            }
        };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        s.Start();
        Assert.Equal("Первое. Второе.", await s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task OnlyInterim_ReturnsInterimAfterQuiet()
    {
        var t = AutoSetup();
        var baseHandler = t.OnSent!;
        t.OnSent = (msg, tr) =>
        {
            baseHandler(msg, tr);
            if (msg.Contains("audioStreamEnd"))
            {
                tr.ServerSends("""{"serverContent":{"interimInputTranscription":{"text":"почти готово"}}}""");
            }
        };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        var interims = new List<string>();
        s.InterimText += x => { lock (interims) { interims.Add(x); } };
        s.Start();
        Assert.Equal("почти готово", await s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Contains("почти готово", interims);
    }

    [Fact]
    public async Task NoTranscriptUntilHardTimeout_ThrowsTimeout()
    {
        var t = AutoSetup(); // setupComplete есть, но ни одного текстового события
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        s.Start();
        s.PushAudio(new byte[3200]);
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(EngineErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task QuotaClose_ThrowsQuotaExceeded()
    {
        var t = new FakeWebSocketTransport();
        t.OnSent = (msg, tr) =>
        {
            if (msg.Contains("\"setup\""))
            {
                tr.ServerCloses(1011, "You exceeded your current quota, please check your plan and billing details.");
            }
        };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        s.Start();
        s.PushAudio(new byte[3200]);
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(EngineErrorKind.QuotaExceeded, ex.Kind);
    }

    [Fact]
    public async Task ConnectFailure_ThrowsNetwork()
    {
        var t = new FakeWebSocketTransport { ConnectException = new TranscriptionException(EngineErrorKind.Network, "нет сети") };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        s.Start();
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(EngineErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task UnexpectedTransportException_IsWrappedAsNetwork()
    {
        var t = new FakeWebSocketTransport { ConnectException = new System.Net.WebSockets.WebSocketException("обрыв") };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        s.Start();
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(EngineErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task NoSetupComplete_ThrowsTimeout()
    {
        var t = new FakeWebSocketTransport();
        await using var s = new GeminiLiveSession(t, Conn(), Fast with { SetupTimeoutMs = 200 });
        s.Start();
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(EngineErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task AbnormalCloseAfterEnd_WithFinalText_ReturnsText()
    {
        var t = AutoSetup();
        var baseHandler = t.OnSent!;
        t.OnSent = (msg, tr) =>
        {
            baseHandler(msg, tr);
            if (msg.Contains("audioStreamEnd"))
            {
                tr.ServerSends("""{"serverContent":{"inputTranscription":{"text":"Успели."}}}""");
                tr.ServerCloses(1011, "Internal error occurred.");
            }
        };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        s.Start();
        Assert.Equal("Успели.", await s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Dispose_CancelsPendingCompletion_AndDisposesTransport()
    {
        var t = new FakeWebSocketTransport(); // setupComplete так и не придёт
        var s = new GeminiLiveSession(t, Conn(), Fast with { SetupTimeoutMs = 10_000 });
        s.Start();
        var completion = s.CompleteAsync(CancellationToken.None);
        await s.DisposeAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.WaitAsync(TestTimeout));
        Assert.True(t.Disposed);
    }

    [Fact]
    public async Task Failed_IsRaisedOnce_OnError_AndNotOnSuccess()
    {
        var failing = new FakeWebSocketTransport { OnSent = (m, tr) => tr.ServerCloses(1011, "You exceeded your current quota") };
        await using var bad = new GeminiLiveSession(failing, Conn(), Fast);
        var failures = new List<EngineErrorKind>();
        bad.Failed += e => failures.Add(e.Kind);
        bad.Start();
        await Assert.ThrowsAsync<TranscriptionException>(() => bad.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal([EngineErrorKind.QuotaExceeded], failures);

        var ok = AutoSetup();
        var baseHandler = ok.OnSent!;
        ok.OnSent = (msg, tr) =>
        {
            baseHandler(msg, tr);
            if (msg.Contains("audioStreamEnd"))
            {
                tr.ServerSends("""{"serverContent":{"inputTranscription":{"text":"Да."},"turnComplete":true}}""");
            }
        };
        await using var good = new GeminiLiveSession(ok, Conn(), Fast);
        var raised = false;
        good.Failed += _ => raised = true;
        good.Start();
        Assert.Equal("Да.", await good.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.False(raised);
    }

    [Fact]
    public async Task ServerCloseMidRecording_FailsWithoutWaitingForKeyRelease()
    {
        var t = AutoSetup();
        var baseHandler = t.OnSent!;
        t.OnSent = (msg, tr) =>
        {
            baseHandler(msg, tr);
            if (msg.Contains("realtimeInput"))
            {
                tr.ServerCloses(1011, "You exceeded your current quota");
            }
        };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        var failed = new TaskCompletionSource<EngineErrorKind>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.Failed += e => failed.TrySetResult(e.Kind);
        s.Start();

        // Запись продолжается (CompleteAsync не вызван), но ошибка должна прийти раньше.
        var pusher = Task.Run(async () =>
        {
            while (!failed.Task.IsCompleted)
            {
                s.PushAudio(new byte[3200]);
                await Task.Delay(10);
            }
        });
        Assert.Equal(EngineErrorKind.QuotaExceeded, await failed.Task.WaitAsync(TestTimeout));
        await pusher.WaitAsync(TestTimeout);
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(EngineErrorKind.QuotaExceeded, ex.Kind);
    }

    [Fact]
    public async Task UnexpectedException_DoesNotLeaveCompletionHanging()
    {
        var t = new FakeWebSocketTransport { ConnectException = new InvalidOperationException("сбой") };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        s.Start();
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(EngineErrorKind.Protocol, ex.Kind);
    }

    [Fact]
    public async Task ThrowingFailedSubscriber_DoesNotBlockCompletionError()
    {
        var t = new FakeWebSocketTransport { ConnectException = new TranscriptionException(EngineErrorKind.Network, "нет сети") };
        await using var s = new GeminiLiveSession(t, Conn(), Fast);
        var second = 0;
        s.Failed += _ => throw new InvalidOperationException("подписчик");
        s.Failed += _ => Interlocked.Increment(ref second);
        s.Start();
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => s.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        Assert.Equal(EngineErrorKind.Network, ex.Kind);
        Assert.Equal(1, Volatile.Read(ref second));
    }

    [Fact]
    public async Task Dispose_IsIdempotent_AndStartAfterDisposeIsIgnored()
    {
        var t = new FakeWebSocketTransport();
        var s = new GeminiLiveSession(t, Conn(), Fast);
        await s.DisposeAsync();
        await s.DisposeAsync();
        s.Start();
        s.Start();
        Assert.Null(t.ConnectedUri);
    }

    [Fact]
    public void Engine_WithoutKeys_ThrowsNotConfigured()
    {
        var engine = new GeminiLiveEngine(() => new FakeWebSocketTransport(), new GeminiKeyRing(new ManualTimeProvider()), _ => Conn(), Fast);
        Assert.Equal(EngineErrorKind.NotConfigured, Assert.Throws<TranscriptionException>(() => engine.StartSession()).Kind);
    }

    [Fact]
    public async Task Engine_QuotaOnFirstKey_NextSessionUsesSecondKey_ThenAllPaused()
    {
        var ring = new GeminiKeyRing(new ManualTimeProvider());
        ring.SetKeys(["key-1", "key-2"]);
        var transports = new List<FakeWebSocketTransport>();
        var engine = new GeminiLiveEngine(() =>
        {
            var t = new FakeWebSocketTransport { OnSent = (m, tr) => tr.ServerCloses(1011, "You exceeded your current quota") };
            transports.Add(t);
            return t;
        }, ring, key => Conn() with { ApiKey = key }, Fast);

        await using (var first = engine.StartSession())
        {
            await Assert.ThrowsAsync<TranscriptionException>(() => first.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        }

        await using (var second = engine.StartSession())
        {
            await Assert.ThrowsAsync<TranscriptionException>(() => second.CompleteAsync(CancellationToken.None).WaitAsync(TestTimeout));
        }

        Assert.Equal("key-1", transports[0].Headers!["x-goog-api-key"]);
        Assert.Equal("key-2", transports[1].Headers!["x-goog-api-key"]);
        var ex = Assert.Throws<TranscriptionException>(() => engine.StartSession());
        Assert.Equal(EngineErrorKind.QuotaExceeded, ex.Kind);
        Assert.Equal(2, transports.Count); // когда все ключи на паузе — без обращения к сети
    }
}
