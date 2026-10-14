using System.Text;
using SaySo.Core.Engines.Gemini;

namespace SaySo.Core.Tests.Engines.Gemini;

public class GeminiServerParserTests
{
    private static IReadOnlyList<GeminiServerEvent> P(string json) => GeminiServerParser.Parse(Encoding.UTF8.GetBytes(json));

    [Theory]
    [InlineData("""{"setupComplete":{}}""")]
    [InlineData("""{"setupComplete":{"sessionId":"abc"}}""")]
    public void SetupComplete(string json) =>
        Assert.Equal([new GeminiServerEvent(GeminiEventKind.SetupComplete)], P(json));

    [Fact]
    public void Interim() =>
        Assert.Equal([new GeminiServerEvent(GeminiEventKind.Interim, "Встре")],
            P("""{"serverContent":{"interimInputTranscription":{"text":"Встре"}}}"""));

    [Fact]
    public void Final() =>
        Assert.Equal([new GeminiServerEvent(GeminiEventKind.Final, "Встретимся в субботу.")],
            P("""{"serverContent":{"inputTranscription":{"text":"Встретимся в субботу.","languageCode":"ru-RU"}}}"""));

    [Fact]
    public void FinalWithFinished_AlsoEmitsTurnComplete() =>
        Assert.Equal(
            [new GeminiServerEvent(GeminiEventKind.Final, "Да."), new GeminiServerEvent(GeminiEventKind.TurnComplete)],
            P("""{"serverContent":{"inputTranscription":{"text":"Да.","finished":true}}}"""));

    [Fact]
    public void InterimAndFinalInOneMessage_InterimComesFirst() =>
        Assert.Equal(
            [new GeminiServerEvent(GeminiEventKind.Interim, "б"), new GeminiServerEvent(GeminiEventKind.Final, "а")],
            P("""{"serverContent":{"inputTranscription":{"text":"а"},"interimInputTranscription":{"text":"б"}}}"""));

    [Theory]
    [InlineData("""{"serverContent":{"turnComplete":true}}""")]
    [InlineData("""{"serverContent":{"generationComplete":true}}""")]
    [InlineData("""{"serverContent":{"interactionStatus":"IDLE"}}""")]
    [InlineData("""{"usageMetadata":{"totalTokenCount":5},"serverContent":{"turnComplete":true}}""")]
    public void TurnComplete(string json) =>
        Assert.Equal([new GeminiServerEvent(GeminiEventKind.TurnComplete)], P(json));

    [Theory]
    [InlineData("""{"serverContent":{"turnComplete":false}}""")]
    [InlineData("""{"usageMetadata":{"totalTokenCount":0}}""")]
    [InlineData("""{"sessionResumptionUpdate":{"newHandle":"x","resumable":true}}""")]
    [InlineData("""[]""")]
    [InlineData("""не json""")]
    [InlineData("""{"serverContent":{"inputTranscription":{"text":null}}}""")]
    public void Ignored(string json) => Assert.Empty(P(json));

    [Theory]
    [InlineData("50s", 50.0)]
    [InlineData("29.5s", 29.5)]
    public void GoAway(string timeLeft, double seconds) =>
        Assert.Equal([new GeminiServerEvent(GeminiEventKind.GoAway, null, TimeSpan.FromSeconds(seconds))],
            P($$$"""{"goAway":{"timeLeft":"{{{timeLeft}}}"}}"""));

    [Fact]
    public void GoAway_WithoutTimeLeft() =>
        Assert.Equal([new GeminiServerEvent(GeminiEventKind.GoAway)], P("""{"goAway":{}}"""));
}
