using SaySo.Core.Interop;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class TrayIconRetryTests
{
    private readonly List<uint> _calls = [];
    private readonly List<(TimeSpan Delay, Action Action, bool Disposed)> _timers = [];
    private int _addFailures;
    private bool _exists; // значок реально есть в трее (модель Проводника)

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
            switch (message)
            {
                case Native.NIM_ADD:
                    if (_exists)
                    {
                        return false; // дубликат
                    }

                    if (_addFailures > 0)
                    {
                        _addFailures--;
                        return false;
                    }

                    _exists = true;
                    return true;
                case Native.NIM_MODIFY:
                    return _exists;
                case Native.NIM_DELETE:
                    var was = _exists;
                    _exists = false;
                    return was;
                default:
                    return true;
            }
        });

    private void FireLastTimer()
    {
        var (_, action, disposed) = _timers[^1];
        Assert.False(disposed);
        action();
    }

    private int Count(uint message) => _calls.Count(c => c == message);

    [Fact]
    public void Hidden_RemovesIcon_KeepsState_AndShowsItBack()
    {
        using var tray = Create();
        tray.Set(1, "SaySo");
        Assert.True(_exists);

        tray.SetVisible(false);
        Assert.False(_exists);
        Assert.False(tray.IsAdded);

        tray.Set(2, "Запись");  // смена состояния, пока значок скрыт
        tray.Recreate();          // перезапуск Проводника
        Assert.False(_exists);
        Assert.Equal(1, Count(Native.NIM_ADD));

        tray.SetVisible(true);
        Assert.True(_exists);
        Assert.True(tray.IsAdded);
        Assert.Equal(2, Count(Native.NIM_ADD));
    }

    [Fact]
    public void HiddenBeforeFirstSet_NeverAdds()
    {
        using var tray = Create();
        tray.SetVisible(false);
        tray.Set(1, "SaySo");
        Assert.Empty(_calls);
        Assert.False(tray.ShowBalloon("t", "x"));
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
    public void FirstAttempt_IsPlainAdd_WithoutModifyProbe()
    {
        _addFailures = 1;
        var icon = Create();

        icon.Set(1, "SaySo");

        Assert.Equal(0, Count(Native.NIM_MODIFY)); // не удваиваем возможную блокировку на занятом Проводнике
    }

    [Fact]
    public void LateAddThatActuallyWentThrough_IsAdoptedByModify_NotLostAsDuplicate()
    {
        _addFailures = 1;
        var icon = Create();
        icon.Set(1, "SaySo");
        Assert.False(icon.IsAdded);
        _exists = true; // Проводник обработал запоздавший NIM_ADD уже после того, как он вернул FALSE

        FireLastTimer();

        Assert.True(icon.IsAdded);
        Assert.Equal(1, Count(Native.NIM_ADD)); // повторного NIM_ADD (дубликата) не было
        Assert.Contains(Native.NIM_SETVERSION, _calls);
        Assert.Single(_timers); // новых повторов нет
    }

    [Fact]
    public void LateAdd_AfterRetriesExhausted_NextSetStillAdoptsIcon()
    {
        _addFailures = int.MaxValue;
        var icon = Create();
        icon.Set(1, "SaySo");
        var fired = 0;
        while (_timers.Count > fired)
        {
            fired++;
            _timers[fired - 1].Action();
        }

        Assert.False(icon.IsAdded);
        _exists = true;

        icon.Set(2, "SaySo: запись…"); // смена состояния значка после исчерпания повторов

        Assert.True(icon.IsAdded);
        icon.Set(1, "SaySo");
        Assert.True(Count(Native.NIM_MODIFY) >= 2); // дальше обычные NIM_MODIFY, а не вечные NIM_ADD
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
        icon.Set(2, "SaySo: запись…");
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
        var addsBefore = Count(Native.NIM_ADD);

        stale();

        Assert.Equal(addsBefore, Count(Native.NIM_ADD));
    }

    [Fact]
    public void Recreate_AfterFailedAdd_DeletesThenAddsWithoutModifyProbe()
    {
        _addFailures = 1;
        var icon = Create();
        icon.Set(1, "SaySo");
        _calls.Clear();

        icon.Recreate();

        Assert.Equal([Native.NIM_DELETE, Native.NIM_ADD, Native.NIM_SETVERSION], _calls);
        Assert.True(icon.IsAdded);
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
