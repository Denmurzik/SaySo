using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class TrayIconRetryTests
{
    private readonly List<uint> _calls = [];
    private readonly List<(TimeSpan Delay, Action Action, bool Disposed)> _timers = [];
    private int _addFailures;

    private unsafe TrayIcon Create(bool withTimer = true) => new(0, 0x8000,
        withTimer ? (delay, action) =>
        {
            _timers.Add((delay, action, false));
            var index = _timers.Count - 1;
            return new Handle(() => _timers[index] = _timers[index] with { Disposed = true });
        } : null,
        (message, _) =>
        {
            _calls.Add(message);
            return message != Native.NIM_ADD || _addFailures-- <= 0;
        });

    private void FireLastTimer()
    {
        var (_, action, disposed) = _timers[^1];
        Assert.False(disposed);
        action();
    }

    [Fact]
    public void AddFails_RetriesAfterTimer_UntilAdded()
    {
        _addFailures = 2;
        var icon = Create();

        icon.Set(1, "SaySo");
        Assert.False(icon.IsAdded);
        Assert.Equal([TrayIcon.RetryDelays[0]], _timers.Select(t => t.Delay));

        FireLastTimer(); // вторая попытка тоже неудачна
        Assert.False(icon.IsAdded);
        Assert.Equal([TrayIcon.RetryDelays[0], TrayIcon.RetryDelays[1]], _timers.Select(t => t.Delay));

        FireLastTimer(); // третья удалась
        Assert.True(icon.IsAdded);
        Assert.Equal(2, _timers.Count); // дальше таймеров нет
        Assert.Contains(Native.NIM_SETVERSION, _calls);
    }

    [Fact]
    public void RetriesAreBounded()
    {
        _addFailures = int.MaxValue;
        var icon = Create();

        icon.Set(1, "SaySo");
        var fired = 0;
        while (_timers.Count > fired) // срабатываем, пока каждый повтор порождает следующий
        {
            fired++;
            _timers[fired - 1].Action();
        }

        Assert.Equal(TrayIcon.RetryDelays.Length, _timers.Count);
        Assert.Equal(TrayIcon.RetryDelays, _timers.Select(t => t.Delay));
        Assert.False(icon.IsAdded);
    }

    [Fact]
    public void SetWhileRetryPending_DoesNotScheduleAnotherTimer()
    {
        _addFailures = 5;
        var icon = Create();

        icon.Set(1, "SaySo");
        icon.Set(2, "SaySo — запись…");
        icon.Set(1, "SaySo");

        Assert.Single(_timers);
    }

    [Fact]
    public void Added_NoTimersAtAll()
    {
        var icon = Create();

        icon.Set(1, "SaySo");

        Assert.True(icon.IsAdded);
        Assert.Empty(_timers);
    }

    [Fact]
    public void DisposeWhileRetryPending_CancelsTimer()
    {
        _addFailures = 1;
        var icon = Create();
        icon.Set(1, "SaySo");

        icon.Dispose();

        Assert.True(_timers[0].Disposed);
    }

    [Fact]
    public void StaleTimerAfterAdded_DoesNothing()
    {
        _addFailures = 1;
        var icon = Create();
        icon.Set(1, "SaySo");
        var stale = _timers[0].Action;
        icon.Recreate(); // например, TaskbarCreated добавил значок раньше таймера
        Assert.True(icon.IsAdded);
        var addsBefore = _calls.Count(c => c == Native.NIM_ADD);

        stale();

        Assert.Equal(addsBefore, _calls.Count(c => c == Native.NIM_ADD));
    }

    [Fact]
    public void WithoutTimerFactory_FailureIsJustLogged()
    {
        _addFailures = 1;
        var icon = Create(withTimer: false);

        icon.Set(1, "SaySo");

        Assert.False(icon.IsAdded);
        Assert.Empty(_timers);
    }

    private sealed class Handle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
