using System.Net;
using SaySo.Core.Engines.Groq;

namespace SaySo.Core.Tests.Engines.Groq;

public class GroqFallbackEngineTests
{
    private static GroqFallbackOptions Opts(bool cleanup = true) =>
        new("whisper-large-v3", "ru", null, cleanup, "openai/gpt-oss-120b");

    [Fact]
    public async Task TranscribesThenCleans()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"text":"ну короче встретимся в субботу"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"choices":[{"message":{"content":"Встретимся в субботу."}}]}""");
        var engine = new GroqFallbackEngine(new GroqClient(new HttpClient(handler), "https://g.test/v1", "k"), Opts());

        Assert.Equal("Встретимся в субботу.", await engine.TranscribeAsync([1, 2], TestContext.Current.CancellationToken));
        Assert.Equal("groq", engine.Name);
    }

    [Fact]
    public async Task CleanupFailure_ReturnsRawText()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"text":"сырой текст"}""");
        handler.Enqueue(HttpStatusCode.TooManyRequests, """{"error":{"message":"rate limit"}}""");
        var engine = new GroqFallbackEngine(new GroqClient(new HttpClient(handler), "https://g.test/v1", "k"), Opts());

        Assert.Equal("сырой текст", await engine.TranscribeAsync([1, 2], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyCleanupResult_ReturnsRawText()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"text":"сырой текст"}""");
        handler.Enqueue(HttpStatusCode.OK, """{"choices":[{"message":{"content":"   "},"finish_reason":"length"}]}""");
        var engine = new GroqFallbackEngine(new GroqClient(new HttpClient(handler), "https://g.test/v1", "k"), Opts());

        Assert.Equal("сырой текст", await engine.TranscribeAsync([1, 2], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Hallucination_SkipsCleanupAndReturnsEmpty()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"text":"Продолжение следует..."}""");
        var engine = new GroqFallbackEngine(new GroqClient(new HttpClient(handler), "https://g.test/v1", "k"), Opts());

        Assert.Equal("", await engine.TranscribeAsync([1, 2], TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task CleanupDisabled_ReturnsWhisperText()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{"text":"как есть"}""");
        var engine = new GroqFallbackEngine(new GroqClient(new HttpClient(handler), "https://g.test/v1", "k"), Opts(cleanup: false));

        Assert.Equal("как есть", await engine.TranscribeAsync([1, 2], TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }
}
