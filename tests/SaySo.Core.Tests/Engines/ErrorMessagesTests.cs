using SaySo.Core.Engines;

namespace SaySo.Core.Tests.Engines;

public class ErrorMessagesTests
{
    [Fact]
    public void EveryKind_HasRussianMessageWithEngineName()
    {
        foreach (var kind in Enum.GetValues<EngineErrorKind>())
        {
            var text = ErrorMessages.Describe(kind, "Gemini");
            Assert.StartsWith("Gemini: ", text);
            Assert.True(text.Length > "Gemini: ".Length + 5);
        }
    }

    [Fact]
    public void Quota() => Assert.Equal("Gemini: лимит запросов исчерпан", ErrorMessages.Describe(EngineErrorKind.QuotaExceeded, "Gemini"));

    [Fact]
    public void Exception_CarriesKind() =>
        Assert.Equal(EngineErrorKind.Timeout, new TranscriptionException(EngineErrorKind.Timeout, "x").Kind);
}
