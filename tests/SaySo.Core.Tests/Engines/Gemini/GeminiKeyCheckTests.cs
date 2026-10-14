using System.Net.WebSockets;
using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Settings;

namespace SaySo.Core.Tests.Engines.Gemini;

public class GeminiKeyCheckTests
{
    private static GeminiConnection Conn() => new(new Uri("wss://example.test/ws"), "KEY",
        new GeminiSessionOptions("gemini-3.5-transcribe-live", TranscriptionMode.Smart, [], []));

    [Fact]
    public async Task SetupComplete_MeansOk()
    {
        var t = new FakeWebSocketTransport { OnSent = (m, tr) => tr.ServerSends("""{"setupComplete":{}}""") };
        Assert.Null(await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(t.Disposed);
        Assert.Equal("KEY", t.Headers!["x-goog-api-key"]);
    }

    [Fact]
    public async Task InvalidKeyClose_IsAuthFailed()
    {
        var t = new FakeWebSocketTransport { OnSent = (m, tr) => tr.ServerCloses(1008, "API key not valid. Please pass a valid API key.") };
        Assert.Equal(EngineErrorKind.AuthFailed, await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Silence_IsTimeout()
    {
        var t = new FakeWebSocketTransport();
        Assert.Equal(EngineErrorKind.Timeout, await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectError_IsReported()
    {
        var t = new FakeWebSocketTransport { ConnectException = new TranscriptionException(EngineErrorKind.RegionBlocked, "регион") };
        Assert.Equal(EngineErrorKind.RegionBlocked, await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExternalCancellation_Propagates_NotTimeout()
    {
        var t = new FakeWebSocketTransport();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(30), cts.Token));
        Assert.True(t.Disposed);
    }

    [Fact]
    public async Task WebSocketException_IsNetwork()
    {
        var t = new FakeWebSocketTransport { ConnectException = new WebSocketException("сбой") };
        Assert.Equal(EngineErrorKind.Network, await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(t.Disposed);
    }

    [Fact]
    public async Task TransportDisposed_OnErrorAndTimeoutPaths()
    {
        var closed = new FakeWebSocketTransport { OnSent = (m, tr) => tr.ServerCloses(1008, "API key not valid.") };
        await GeminiKeyCheck.CheckAsync(closed, Conn(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(closed.Disposed);

        var silent = new FakeWebSocketTransport();
        await GeminiKeyCheck.CheckAsync(silent, Conn(), TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
        Assert.True(silent.Disposed);

        var connectFail = new FakeWebSocketTransport { ConnectException = new TranscriptionException(EngineErrorKind.RegionBlocked, "регион") };
        await GeminiKeyCheck.CheckAsync(connectFail, Conn(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(connectFail.Disposed);
    }

    [Fact]
    public async Task Key_GoesInHeader_NotInUri()
    {
        var t = new FakeWebSocketTransport { OnSent = (m, tr) => tr.ServerSends("""{"setupComplete":{}}""") };
        await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("KEY", t.ConnectedUri!.ToString());
        Assert.Equal("KEY", t.Headers!["x-goog-api-key"]);
        Assert.DoesNotContain(t.Sent, m => m.Contains("KEY"));
    }
}
