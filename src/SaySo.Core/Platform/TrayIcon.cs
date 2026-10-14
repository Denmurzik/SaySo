using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>Вызов Shell_NotifyIcon; в тестах подменяется, чтобы не трогать настоящий трей.</summary>
internal unsafe delegate bool TrayNotify(uint message, Native.NOTIFYICONDATAW* data);

/// <summary>Значок в области уведомлений (NOTIFYICON_VERSION_4: событие — в LOWORD(lParam)).</summary>
/// <param name="retryTimer">
/// Одноразовый таймер окна для повторов NIM_ADD: при входе в систему Проводник бывает занят и Shell_NotifyIcon возвращает
/// FALSE, а TaskbarCreated после этого уже не придёт. Повторов немного и только пока значок не добавлен — «ноль таймеров
/// в простое» не нарушается. null — без повторов.
/// </param>
/// <param name="notify">Подмена Shell_NotifyIcon для тестов; null — настоящий вызов.</param>
internal sealed unsafe class TrayIcon(
    nint hwnd,
    uint callbackMessage,
    Func<TimeSpan, Action, IDisposable>? retryTimer = null,
    TrayNotify? notify = null) : IDisposable
{
    /// <summary>Паузы перед повторами NIM_ADD (после первой неудачи).</summary>
    internal static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private readonly TrayNotify _notify = notify ?? ((message, data) => Native.Shell_NotifyIconW(message, data));
    private bool _added;
    private bool _configured;
    private nint _icon;
    private string _tooltip = "SaySo";
    private int _retries;
    private IDisposable? _retryTimer;

    internal bool IsAdded => _added;

    /// <param name="icon">HICON или 0: без значка запись в трее всё равно создаётся (подсказка и меню работают).</param>
    public void Set(nint icon, string tooltip)
    {
        _configured = true;
        _icon = icon;
        _tooltip = tooltip;
        var data = Build();
        if (_added)
        {
            _notify(Native.NIM_MODIFY, &data);
            return;
        }

        if (!_notify(Native.NIM_ADD, &data))
        {
            Log.Info("Shell_NotifyIcon(NIM_ADD) не удался — повтор позже и после TaskbarCreated");
            ScheduleRetry();
            return;
        }

        data.uVersion = Native.NOTIFYICON_VERSION_4;
        _notify(Native.NIM_SETVERSION, &data);
        _added = true;
        StopRetries();
    }

    /// <summary>После перезапуска Проводника значок нужно добавить заново.</summary>
    public void Recreate()
    {
        if (!_configured)
        {
            return;
        }

        // Если запись в трее ещё жива (TaskbarCreated без реального перезапуска, или NIM_ADD «не удался», но значок есть),
        // NIM_ADD дал бы ошибку и _added навсегда остался бы false — поэтому сначала удаляем.
        var data = Build();
        _notify(Native.NIM_DELETE, &data);
        _added = false;
        StopRetries(); // свежий перезапуск Проводника — свежий запас повторов
        Set(_icon, _tooltip);
    }

    public void Dispose()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        if (!_added)
        {
            return;
        }

        var data = Build();
        _notify(Native.NIM_DELETE, &data);
        _added = false;
    }

    private void ScheduleRetry()
    {
        // Повторные Set (смена состояния значка) не должны ни съедать запас повторов, ни плодить таймеры.
        if (retryTimer is null || _retryTimer is not null || _retries >= RetryDelays.Length)
        {
            return;
        }

        try
        {
            _retryTimer = retryTimer(RetryDelays[_retries++], OnRetry);
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            Log.Error("Повтор добавления значка", e); // без повтора значок вернётся с TaskbarCreated
        }
    }

    private void OnRetry()
    {
        _retryTimer = null; // таймер одноразовый, уже сработал
        if (!_added && _configured)
        {
            Set(_icon, _tooltip);
        }
    }

    private void StopRetries()
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        _retries = 0;
    }

    private Native.NOTIFYICONDATAW Build()
    {
        var data = new Native.NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
            hWnd = hwnd,
            uID = 1,
            uFlags = Native.NIF_MESSAGE | Native.NIF_TIP | Native.NIF_SHOWTIP | (_icon != 0 ? Native.NIF_ICON : 0),
            uCallbackMessage = callbackMessage,
            hIcon = _icon,
        };
        var tip = TrayTooltip.Fit(_tooltip);
        tip.AsSpan().CopyTo(new Span<char>(data.szTip, 128));
        return data;
    }
}
