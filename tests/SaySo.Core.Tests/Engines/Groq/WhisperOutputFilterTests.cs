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
    [InlineData("you")]
    [InlineData(" You. ")]
    public void Hallucinations_BecomeEmpty(string input) => Assert.Equal("", WhisperOutputFilter.Clean(input));

    [Theory]
    [InlineData("Продолжение следует, сказал он, и вышел.")]
    [InlineData("Спасибо за просмотр презентации, коллеги, вопросы?")]
    [InlineData("Купить молоко.")]
    [InlineData("you know what I mean")]
    [InlineData("Спасибо за просмотр. Теперь перейдём к следующему слайду")]
    [InlineData("Продолжение следует в следующей главе про архитектуру и тестирование")]
    public void RealSpeech_IsKept(string input) => Assert.Equal(input, WhisperOutputFilter.Clean(input));
}
