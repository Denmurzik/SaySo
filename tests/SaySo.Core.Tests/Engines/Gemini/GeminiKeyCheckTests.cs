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
        Assert.Null(await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(5)));
        Assert.True(t.Disposed);
        Assert.Equal("KEY", t.Headers!["x-goog-api-key"]);
    }

    [Fact]
    public async Task InvalidKeyClose_IsAuthFailed()
    {
        var t = new FakeWebSocketTransport { OnSent = (m, tr) => tr.ServerCloses(1008, "API key not valid. Please pass a valid API key.") };
        Assert.Equal(EngineErrorKind.AuthFailed, await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Silence_IsTimeout()
    {
        var t = new FakeWebSocketTransport();
        Assert.Equal(EngineErrorKind.Timeout, await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task ConnectError_IsReported()
    {
        var t = new FakeWebSocketTransport { ConnectException = new TranscriptionException(EngineErrorKind.RegionBlocked, "регион") };
        Assert.Equal(EngineErrorKind.RegionBlocked, await GeminiKeyCheck.CheckAsync(t, Conn(), TimeSpan.FromSeconds(5)));
    }
}
