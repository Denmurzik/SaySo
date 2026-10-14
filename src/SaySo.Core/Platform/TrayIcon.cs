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
    private bool _addFailed; // был неудачный NIM_ADD: значок мог появиться позже
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

        // После неудачного NIM_ADD значок мог всё же появиться (FALSE по таймауту, а Проводник обработал запрос позже): тогда
        // повторный NIM_ADD вернул бы FALSE как дубликат, и _added навсегда остался бы false. Поэтому сначала пробуем изменить
        // существующий значок; если его нет, NIM_MODIFY просто вернёт FALSE.
        var exists = _addFailed && _notify(Native.NIM_MODIFY, &data);
        if (!exists && !_notify(Native.NIM_ADD, &data))
        {
            _addFailed = true;
            Log.Info("Shell_NotifyIcon(NIM_ADD) не удался — повтор позже и после TaskbarCreated");
            ScheduleRetry();
            return;
        }

        data.uVersion = Native.NOTIFYICON_VERSION_4;
        _notify(Native.NIM_SETVERSION, &data);
        _added = true;
        _addFailed = false;
        StopRetries();
    }

    /// <summary>
    /// Всплывающее уведомление у значка (NIF_INFO). События — NIN_BALLOONUSERCLICK / NIN_BALLOONTIMEOUT в <c>TrayEvent</c>.
    /// NIIF_RESPECT_QUIET_TIME: не показывать в «тихий час» первого входа в новую учётную запись; режим «Не беспокоить»
    /// (помощник по фокусировке) подавляет уведомления независимо от этого флага — тогда событий не будет совсем.
    /// </summary>
    /// <returns>false — значка в трее нет (уведомлять некуда).</returns>
    public bool ShowBalloon(string title, string text)
    {
        if (!_added)
        {
            return false;
        }

        var data = Build();
        data.uFlags |= Native.NIF_INFO;
        data.dwInfoFlags = Native.NIIF_INFO | Native.NIIF_RESPECT_QUIET_TIME;
        Fit(title, 64).AsSpan().CopyTo(new Span<char>(data.szInfoTitle, 64));
        Fit(text, 256).AsSpan().CopyTo(new Span<char>(data.szInfo, 256));
        return _notify(Native.NIM_MODIFY, &data);
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
        _addFailed = false; // значок только что удалён: NIM_ADD, без попытки «изменить существующий»
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

    // Буфер под строку вместе с завершающим нулём: длинный текст обрезается, а не портит соседнее поле структуры.
    private static string Fit(string text, int capacity) => text.Length < capacity ? text : text[..(capacity - 1)];

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
