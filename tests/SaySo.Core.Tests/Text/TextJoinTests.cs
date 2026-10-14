using SaySo.Core.Text;

namespace SaySo.Core.Tests.Text;

public class TextJoinTests
{
    [Theory]
    [InlineData("Привет.", "Как дела?", "Привет. Как дела?")]
    [InlineData("Привет", ", мир", "Привет, мир")]
    [InlineData("Цена 5", "%", "Цена 5%")]
    [InlineData("Строка\n", "Новая", "Строка\nНовая")]
    [InlineData("Текст ", "ещё", "Текст ещё")]
    [InlineData("(", "скобка", "(скобка")]
    [InlineData("", "Начало", "Начало")]
    [InlineData("Конец", "", "Конец")]
    public void Join(string left, string right, string expected) => Assert.Equal(expected, TextJoin.Join(left, right));
}
