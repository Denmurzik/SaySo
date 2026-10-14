using SaySo.Core.Engines.Gemini;
using SaySo.Core.Settings;

namespace SaySo.Core.Tests.Engines.Gemini;

public class GeminiConnectionTests
{
    private const string Secret = "AIzaSy-секретный-ключ-123";

    private static GeminiConnection Create() => new(
        new Uri("wss://example.test/ws"), Secret, new GeminiSessionOptions("model-x", TranscriptionMode.Smart, ["ru-RU"], ["SaySo"]));

    [Fact]
    public void ToString_MasksApiKey()
    {
        var text = Create().ToString();

        Assert.DoesNotContain(Secret, text);
        Assert.Contains("ApiKey = ***", text);
        Assert.Contains("wss://example.test/ws", text);
        Assert.Contains("model-x", text);
    }

    [Fact]
    public void ToString_DoesNotLeakKeyThroughInterpolationOrOptions() =>
        Assert.DoesNotContain(Secret, $"{Create()} / {Create() with { ApiKey = Secret }}");

    [Fact]
    public void KeyStillTravelsInHeaderOnly()
    {
        var connection = Create();

        Assert.Equal(Secret, connection.Headers["x-goog-api-key"]);
        Assert.DoesNotContain(Secret, connection.ConnectUri.ToString());
    }
}
