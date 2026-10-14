using System.Net;
using SaySo.Core.Dictation;
using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Engines.Groq;
using SaySo.Core.Settings;
using SaySo.Core.Storage;
using SaySo.Core.Tests.Engines.Gemini;
using SaySo.Core.Tests.Engines.Groq;

namespace SaySo.Core.Tests.Engines;

/// <summary>Неверный адрес эндпоинта в settings.json — понятная ошибка движка, а не UriFormatException.</summary>
public class InvalidEndpointTests
{
    private static readonly GeminiSessionOptions Options = new("m", TranscriptionMode.Smart, [], []);

    [Theory]
    [InlineData("не адрес")]
    [InlineData("generativelanguage.googleapis.com/ws")]
    [InlineData("https://example.test/ws")]
    [InlineData("")]
    public void GeminiConnection_BadEndpoint_ThrowsInvalidEndpoint(string endpoint)
    {
        var ex = Assert.Throws<TranscriptionException>(() => GeminiConnection.Create(endpoint, "k", Options));

        Assert.Equal(EngineErrorKind.InvalidEndpoint, ex.Kind);
        if (endpoint.Length > 0)
        {
            Assert.DoesNotContain(endpoint, ex.Message); // адрес из настроек в текст ошибки не попадает
        }
    }

    [Fact]
    public void GeminiConnection_GoodEndpoint_IsParsed() =>
        Assert.Equal("wss", GeminiConnection.Create(RecognitionSettings.DefaultGeminiEndpoint, "k", Options).Endpoint.Scheme);

    [Fact]
    public void SettingsEngines_BadGeminiEndpoint_StartSessionThrowsInvalidEndpoint()
    {
        var settings = new AppSettings();
        settings.Recognition.GeminiEndpoint = "http:// not a url";
        var engines = new SettingsDictationEngines(() => settings, () => new Secrets { GeminiApiKeys = ["g"] },
            new HttpClient(), () => new FakeWebSocketTransport(), new ManualTimeProvider());

        var ex = Assert.Throws<TranscriptionException>(() => engines.Primary!.StartSession());

        Assert.Equal(EngineErrorKind.InvalidEndpoint, ex.Kind);
    }

    [Fact]
    public async Task DictationRun_BadGeminiEndpoint_FallsBackToGroq_AndNamesTheReason()
    {
        var settings = new AppSettings();
        settings.Recognition.GeminiEndpoint = "мусор";
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"text":"из резерва"}""");
        var engines = new SettingsDictationEngines(() => settings, () => new Secrets { GeminiApiKeys = ["g"], GroqApiKey = "k" },
            new HttpClient(handler), () => new FakeWebSocketTransport(), new ManualTimeProvider());
        settings.Recognition.GroqCleanupEnabled = false;
        await using var run = new DictationPipeline(engines).Begin();
        run.StartPrimary(); // раньше UriFormatException вылетал отсюда, из таймера координатора
        run.PushAudio(TestAudio(1000));

        var result = await run.FinishAsync(TestContext.Current.CancellationToken);

        Assert.Equal("из резерва", result.Text);
        Assert.Equal(EngineErrorKind.InvalidEndpoint, result.PrimaryError);
    }

    [Theory]
    [InlineData("не адрес")]
    [InlineData("/openai/v1")]
    [InlineData("ftp://example.test/v1")]
    public async Task Groq_BadBaseUrl_ThrowsInvalidEndpoint_ForEveryCall(string baseUrl)
    {
        var client = new GroqClient(new HttpClient(new FakeHttpHandler()), baseUrl, "gsk_TEST");
        var ct = TestContext.Current.CancellationToken;

        var transcribe = await Assert.ThrowsAsync<TranscriptionException>(() => client.TranscribeAsync([1, 2], "m", null, null, ct));
        var cleanup = await Assert.ThrowsAsync<TranscriptionException>(() => client.CleanupAsync("текст", "m", ct));
        var validate = await Assert.ThrowsAsync<TranscriptionException>(() => client.ValidateKeyAsync(ct));

        Assert.All([transcribe, cleanup, validate], e => Assert.Equal(EngineErrorKind.InvalidEndpoint, e.Kind));
        Assert.DoesNotContain("gsk_TEST", transcribe.Message);
    }

    private static byte[] TestAudio(int ms)
    {
        var samples = ms * 16;
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(8000 * Math.Sin(2 * Math.PI * 300 * i / 16000.0)));
        }

        return bytes;
    }
}
