using SaySo.Core.Dictation;
using SaySo.Core.Engines;
using SaySo.Core.Settings;
using SaySo.Core.Storage;
using SaySo.Core.Tests.Engines.Gemini;

namespace SaySo.Core.Tests.Dictation;

public class SettingsDictationEnginesTests
{
    private readonly List<FakeWebSocketTransport> _transports = [];

    private SettingsDictationEngines Create(AppSettings s, Secrets secrets) =>
        new(() => s, () => secrets, new HttpClient(), () =>
        {
            var t = new FakeWebSocketTransport();
            _transports.Add(t);
            return t;
        }, new ManualTimeProvider());

    [Fact]
    public void NoGroqKey_NoFallback()
    {
        var e = Create(new AppSettings(), new Secrets { GeminiApiKeys = ["g"] });
        Assert.NotNull(e.Primary);
        Assert.Null(e.Fallback);
    }

    [Fact]
    public void FallbackDisabled_NoFallback()
    {
        var s = new AppSettings();
        s.Recognition.GroqFallbackEnabled = false;
        Assert.Null(Create(s, new Secrets { GroqApiKey = "k" }).Fallback);
    }

    [Fact]
    public void GroqKey_GivesFallback() =>
        Assert.Equal("groq", Create(new AppSettings(), new Secrets { GroqApiKey = "k" }).Fallback!.Name);

    [Fact]
    public void NoGeminiKeys_PrimaryThrowsNotConfigured()
    {
        var ex = Assert.Throws<TranscriptionException>(() => Create(new AppSettings(), new Secrets()).Primary!.StartSession());
        Assert.Equal(EngineErrorKind.NotConfigured, ex.Kind);
    }

    [Fact]
    public async Task FirstKeyIsUsed_AndRingSurvivesBetweenAccesses()
    {
        var engines = Create(new AppSettings(), new Secrets { GeminiApiKeys = ["key-1", "key-2"] });
        await using (var s = engines.Primary!.StartSession())
        {
            await WaitConnected(0);
            Assert.Equal("key-1", _transports[0].Headers!["x-goog-api-key"]);
        }

        // Ключ 1 исчерпал квоту — следующий доступ к Primary выдаёт ключ 2 (кольцо одно на всё время жизни).
        _transports.Clear();
        var failing = engines.Primary!.StartSession();
        await WaitConnected(0);
        _transports[0].ServerCloses(1011, "You exceeded your current quota");
        await Assert.ThrowsAsync<TranscriptionException>(() => failing.CompleteAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        await failing.DisposeAsync();

        await using var next = engines.Primary!.StartSession();
        await WaitConnected(1);
        Assert.Equal("key-2", _transports[1].Headers!["x-goog-api-key"]);
    }

    private async Task WaitConnected(int index)
    {
        for (var i = 0; i < 200 && (_transports.Count <= index || _transports[index].Headers is null); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
