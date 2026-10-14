namespace SaySo.Core.Platform;

/// <summary>Пропускает действие не чаще одного раза за интервал (например, переустановка хука при двойном щелчке по значку).</summary>
internal sealed class MinIntervalGate(TimeSpan interval, TimeProvider time)
{
    private long? _lastPassed;

    /// <returns>true — действие можно выполнять (и это время записано); false — прошло меньше интервала.</returns>
    public bool TryPass()
    {
        var now = time.GetTimestamp();
        if (_lastPassed is { } last && time.GetElapsedTime(last, now) < interval)
        {
            return false;
        }

        _lastPassed = now;
        return true;
    }
}
