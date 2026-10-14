using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class MinIntervalGateTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void First_Passes_SecondWithinInterval_Blocked_AfterIntervalPasses()
    {
        var gate = new MinIntervalGate(TimeSpan.FromSeconds(1), _time);

        Assert.True(gate.TryPass());
        _time.Advance(TimeSpan.FromMilliseconds(400)); // второй щелчок двойного щелчка
        Assert.False(gate.TryPass());
        _time.Advance(TimeSpan.FromMilliseconds(599));
        Assert.False(gate.TryPass());
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(gate.TryPass());
    }

    [Fact]
    public void BlockedAttempt_DoesNotExtendTheInterval()
    {
        var gate = new MinIntervalGate(TimeSpan.FromSeconds(1), _time);
        Assert.True(gate.TryPass());
        _time.Advance(TimeSpan.FromMilliseconds(900));
        Assert.False(gate.TryPass());
        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.True(gate.TryPass()); // отсчёт — от последнего выполненного, а не от последней попытки
    }
}
