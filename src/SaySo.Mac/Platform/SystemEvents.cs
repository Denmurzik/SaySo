using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>
/// События системы для демона (аналог WM_POWERBROADCAST и WTS_SESSION_* в Windows): выход из сна, блокировка и
/// разблокировка экрана, уход и возврат сеанса при быстрой смене пользователя.
/// </summary>
internal sealed class SystemEvents : IDisposable
{
    private readonly Bridge _observer;

    public SystemEvents()
    {
        _observer = new Bridge { Notified = OnNotification };
        var workspace = NS.WorkspaceNotificationCenter;
        NS.Observe(workspace, _observer.Handle, "NSWorkspaceDidWakeNotification");
        NS.Observe(workspace, _observer.Handle, "NSWorkspaceSessionDidResignActiveNotification");
        NS.Observe(workspace, _observer.Handle, "NSWorkspaceSessionDidBecomeActiveNotification");
        var distributed = NS.DistributedNotificationCenter;
        NS.Observe(distributed, _observer.Handle, "com.apple.screenIsLocked");
        NS.Observe(distributed, _observer.Handle, "com.apple.screenIsUnlocked");
    }

    public event Action? Resumed;

    /// <summary>true — экран заблокирован (или сеанс ушёл в фон), false — снова доступен.</summary>
    public event Action<bool>? LockChanged;

    public void Dispose()
    {
        NS.Unobserve(NS.WorkspaceNotificationCenter, _observer.Handle);
        NS.Unobserve(NS.DistributedNotificationCenter, _observer.Handle);
        _observer.Dispose();
    }

    private void OnNotification(nint notification)
    {
        switch (NS.NotificationName(notification))
        {
            case "NSWorkspaceDidWakeNotification":
                Resumed?.Invoke();
                break;
            case "com.apple.screenIsLocked" or "NSWorkspaceSessionDidResignActiveNotification":
                LockChanged?.Invoke(true);
                break;
            case "com.apple.screenIsUnlocked" or "NSWorkspaceSessionDidBecomeActiveNotification":
                LockChanged?.Invoke(false);
                break;
        }
    }
}
