using System.Net;
using System.Text.Json;
using SaySo.Core.Engines;
using SaySo.Core.Engines.Groq;

namespace SaySo.Core.Tests.Engines.Groq;

public class GroqClientTests
{
    private const string Base = "https://api.groq.test/openai/v1";

    private static (GroqClient Client, FakeHttpHandler Handler) Create()
    {
        var handler = new FakeHttpHandler();
        return (new GroqClient(new HttpClient(handler), Base, "gsk_TEST"), handler);
    }

    [Fact]
    public async Task Transcribe_SendsMultipartWithAllFields()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"text":" Привет, мир. ","x_groq":{"id":"req_1"}}""");

        var text = await client.TranscribeAsync([1, 2, 3, 4], "whisper-large-v3", "ru", "SaySo, Gemini", TestContext.Current.CancellationToken);

        Assert.Equal("Привет, мир.", text);
        var (req, raw) = handler.Requests.Single();
        var body = raw.Replace("\"", ""); // name=model и name="model" — оба допустимы в multipart
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(Base + "/audio/transcriptions", req.RequestUri!.ToString());
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal("gsk_TEST", req.Headers.Authorization.Parameter);
        Assert.Contains("name=model", body);
        Assert.Contains("whisper-large-v3", body);
        Assert.Contains("name=response_format", body);
        Assert.Contains("name=temperature", body);
        Assert.Contains("name=language", body);
        Assert.Contains("name=prompt", body);
        Assert.Contains("SaySo, Gemini", body);
        Assert.Contains("filename=audio.wav", body);
        Assert.Contains("audio/wav", body);
    }

    [Fact]
    public async Task Transcribe_OmitsLanguageAndPromptWhenNull()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"text":"ок"}""");
        await client.TranscribeAsync([1, 2], "whisper-large-v3", null, null, TestContext.Current.CancellationToken);
        var body = handler.Requests.Single().Body.Replace("\"", "");
        Assert.DoesNotContain("name=language", body);
        Assert.DoesNotContain("name=prompt", body);
    }

    [Fact]
    public async Task Cleanup_SendsChatRequestForReasoningModel()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK,
            """{"choices":[{"index":0,"message":{"role":"assistant","content":"Встретимся в субботу."},"finish_reason":"stop"}]}""");

        var text = await client.CleanupAsync("ну встретимся в пятницу нет в субботу", "openai/gpt-oss-120b", TestContext.Current.CancellationToken);

        Assert.Equal("Встретимся в субботу.", text);
        var (req, body) = handler.Requests.Single();
        Assert.Equal(Base + "/chat/completions", req.RequestUri!.ToString());
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("openai/gpt-oss-120b", root.GetProperty("model").GetString());
        Assert.Equal("low", root.GetProperty("reasoning_effort").GetString());
        Assert.False(root.GetProperty("include_reasoning").GetBoolean());
        Assert.False(root.TryGetProperty("reasoning_format", out _));
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.True(root.GetProperty("max_completion_tokens").GetInt32() >= 1024);
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal(CleanupPrompt.System, messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("ну встретимся в пятницу нет в субботу", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Cleanup_TruncatedByLength_IsProtocolError()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK,
            """{"choices":[{"message":{"content":"Встретимся в суб"},"finish_reason":"length"}]}""");
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() =>
            client.CleanupAsync("текст", "openai/gpt-oss-120b", TestContext.Current.CancellationToken));
        Assert.Equal(EngineErrorKind.Protocol, ex.Kind);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, EngineErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.Unauthorized, EngineErrorKind.AuthFailed)]
    [InlineData(HttpStatusCode.Forbidden, EngineErrorKind.AuthFailed)]
    [InlineData(HttpStatusCode.NotFound, EngineErrorKind.ModelNotFound)]
    [InlineData(HttpStatusCode.BadGateway, EngineErrorKind.Network)]
    [InlineData(HttpStatusCode.BadRequest, EngineErrorKind.Protocol)]
    public async Task HttpErrors_AreClassified(HttpStatusCode status, EngineErrorKind expected)
    {
        var (client, handler) = Create();
        handler.Enqueue(status, """{"error":{"message":"что-то пошло не так","type":"x"}}""");
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() =>
            client.TranscribeAsync([1, 2], "whisper-large-v3", null, null, TestContext.Current.CancellationToken));
        Assert.Equal(expected, ex.Kind);
        Assert.DoesNotContain("gsk_TEST", ex.Message);
    }

    [Fact]
    public async Task NetworkFailure_IsNetwork()
    {
        var (client, handler) = Create();
        handler.EnqueueException(new HttpRequestException("DNS"));
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() =>
            client.CleanupAsync("текст", "openai/gpt-oss-120b", TestContext.Current.CancellationToken));
        Assert.Equal(EngineErrorKind.Network, ex.Kind);
    }

    [Fact]
    public async Task ValidateKey_CallsModelsEndpoint()
    {
        var (client, handler) = Create();
        handler.Enqueue(HttpStatusCode.OK, """{"object":"list","data":[]}""");
        await client.ValidateKeyAsync(TestContext.Current.CancellationToken);
        Assert.Equal(Base + "/models", handler.Requests.Single().Request.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(new string[0], null)]
    [InlineData(new[] { "ru-RU" }, "ru")]
    [InlineData(new[] { "en" }, "en")]
    [InlineData(new[] { "ru-RU", "en-US" }, null)]
    public void ToWhisperLanguage(string[] codes, string? expected) =>
        Assert.Equal(expected, GroqClient.ToWhisperLanguage(codes));

    [Fact]
    public void BuildVocabularyPrompt_JoinsAndTruncatesAtTermBoundary()
    {
        Assert.Null(GroqClient.BuildVocabularyPrompt([]));
        Assert.Equal("SaySo, Gemini", GroqClient.BuildVocabularyPrompt(["SaySo", "Gemini"]));
        var many = Enumerable.Range(0, 200).Select(i => "термин" + i).ToList();
        var prompt = GroqClient.BuildVocabularyPrompt(many)!;
        Assert.True(prompt.Length <= GroqClient.MaxPromptChars);
        Assert.False(prompt.EndsWith(','));
        Assert.All(prompt.Split(", "), t => Assert.Contains(t, many));
    }
}
