using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>Значок в области уведомлений (NOTIFYICON_VERSION_4: событие — в LOWORD(lParam)).</summary>
internal sealed unsafe class TrayIcon(nint hwnd, uint callbackMessage) : IDisposable
{
    private bool _added;
    private bool _configured;
    private nint _icon;
    private string _tooltip = "SaySo";

    /// <param name="icon">HICON или 0: без значка запись в трее всё равно создаётся (подсказка и меню работают).</param>
    public void Set(nint icon, string tooltip)
    {
        _configured = true;
        _icon = icon;
        _tooltip = tooltip;
        var data = Build();
        if (_added)
        {
            Native.Shell_NotifyIconW(Native.NIM_MODIFY, &data);
            return;
        }

        if (!Native.Shell_NotifyIconW(Native.NIM_ADD, &data))
        {
            Log.Info("Shell_NotifyIcon(NIM_ADD) не удался — значок появится после TaskbarCreated");
            return;
        }

        data.uVersion = Native.NOTIFYICON_VERSION_4;
        Native.Shell_NotifyIconW(Native.NIM_SETVERSION, &data);
        _added = true;
    }

    /// <summary>После перезапуска Проводника значок нужно добавить заново.</summary>
    public void Recreate()
    {
        _added = false;
        if (_configured)
        {
            Set(_icon, _tooltip);
        }
    }

    public void Dispose()
    {
        if (!_added)
        {
            return;
        }

        var data = Build();
        Native.Shell_NotifyIconW(Native.NIM_DELETE, &data);
        _added = false;
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
        var tip = _tooltip.Length > 127 ? _tooltip[..127] : _tooltip;
        tip.AsSpan().CopyTo(new Span<char>(data.szTip, 128));
        return data;
    }
}
