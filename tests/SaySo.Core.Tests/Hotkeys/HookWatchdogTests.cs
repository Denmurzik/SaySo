using SaySo.Core.Hotkeys;

namespace SaySo.Core.Tests.Hotkeys;

public class HookWatchdogTests
{
    [Theory]
    [InlineData(10_000u, 10_000u, false)] // хук видел это же нажатие
    [InlineData(10_900u, 10_000u, false)] // в пределах допуска
    [InlineData(10_000u, 10_500u, false)] // хук видел более позднее нажатие
    [InlineData(12_000u, 10_000u, true)]  // хук молчит две секунды
    [InlineData(100u, uint.MaxValue - 99u, false)] // переход счётчика через ноль: 200 мс
    [InlineData(2_000u, uint.MaxValue - 99u, true)] // переход через ноль: 2,1 с
    public void HookMissed(uint input, uint hook, bool expected) =>
        Assert.Equal(expected, HookWatchdog.HookMissed(input, hook));
}
