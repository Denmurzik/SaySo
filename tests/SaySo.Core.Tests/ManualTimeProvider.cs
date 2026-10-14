namespace SaySo.Core.Tests;

/// <summary>
/// Управляемые часы для тестов пауз и таймаутов. <see cref="Advance"/> двигает и календарное время, и монотонный
/// счётчик (<see cref="GetTimestamp"/>, на нём считает <see cref="TimeProvider.GetElapsedTime(long)"/>).
/// Таймеры (<see cref="TimeProvider.CreateTimer"/>) системные, не управляемые.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _timestamp += by.Ticks;
    }
}
