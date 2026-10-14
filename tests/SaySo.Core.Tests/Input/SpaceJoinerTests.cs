using SaySo.Core.Input;

namespace SaySo.Core.Tests.Input;

public class SpaceJoinerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoPrevious_Unchanged() => Assert.Equal("Привет.", SpaceJoiner.Prepare("Привет.", null, 1, T0));

    [Fact]
    public void SameWindowRecently_AddsSpace() =>
        Assert.Equal(" Как дела?", SpaceJoiner.Prepare("Как дела?", new LastInsertion(1, T0, "Привет."), 1, T0.AddSeconds(10)));

    [Fact]
    public void OtherWindow_NoSpace() =>
        Assert.Equal("Как дела?", SpaceJoiner.Prepare("Как дела?", new LastInsertion(1, T0, "Привет."), 2, T0.AddSeconds(10)));

    [Fact]
    public void TooLate_NoSpace() =>
        Assert.Equal("Как дела?", SpaceJoiner.Prepare("Как дела?", new LastInsertion(1, T0, "Привет."), 1, T0.AddSeconds(61)));

    [Fact]
    public void PreviousEndsWithNewline_NoSpace() =>
        Assert.Equal("Дальше", SpaceJoiner.Prepare("Дальше", new LastInsertion(1, T0, "Строка\n"), 1, T0.AddSeconds(1)));

    [Fact]
    public void StartsWithPunctuation_NoSpace() =>
        Assert.Equal(", и ещё", SpaceJoiner.Prepare(", и ещё", new LastInsertion(1, T0, "Слово"), 1, T0.AddSeconds(1)));
}
