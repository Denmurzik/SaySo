using SaySo.Core.Engines.Groq;

namespace SaySo.Core.Tests.Engines.Groq;

public class WhisperOutputFilterTests
{
    [Theory]
    [InlineData("Продолжение следует...")]
    [InlineData(" Субтитры сделал DimaTorzok ")]
    [InlineData("Субтитры создавал DimaTorzok")]
    [InlineData("Редактор субтитров А.Синецкая Корректор А.Егорова")]
    [InlineData("Спасибо за просмотр!")]
    [InlineData("Thank you for watching.")]
    [InlineData("...")]
    [InlineData("   ")]
    public void Hallucinations_BecomeEmpty(string input) => Assert.Equal("", WhisperOutputFilter.Clean(input));

    [Theory]
    [InlineData("Продолжение следует, сказал он, и вышел.")]
    [InlineData("Спасибо за просмотр презентации, коллеги, вопросы?")]
    [InlineData("Купить молоко.")]
    public void RealSpeech_IsKept(string input) => Assert.Equal(input, WhisperOutputFilter.Clean(input));
}
