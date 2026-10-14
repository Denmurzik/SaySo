using System.ComponentModel;
using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>Окно Win32 с управляемой оконной процедурой (через UnmanagedCallersOnly, совместимо с AOT).</summary>
internal abstract unsafe class NativeWindow : IDisposable
{
    private static readonly Dictionary<nint, NativeWindow> Live = [];
    private static readonly HashSet<string> Registered = [];
    private static readonly object Gate = new();

    public nint Handle { get; private set; }

    public virtual void Dispose()
    {
        var hwnd = Handle;
        if (hwnd == 0)
        {
            return;
        }

        Handle = 0;
        lock (Gate)
        {
            Live.Remove(hwnd);
        }

        Native.DestroyWindow(hwnd);
    }

    protected void CreateHandle(string className, uint exStyle, uint style, nint parent, string? title = null)
    {
        var instance = Native.GetModuleHandleW(null);
        lock (Gate)
        {
            if (!Registered.Contains(className))
            {
                fixed (char* name = className)
                {
                    var wc = new Native.WNDCLASSEXW
                    {
                        cbSize = (uint)sizeof(Native.WNDCLASSEXW),
                        lpfnWndProc = &WndProcThunk,
                        hInstance = instance,
                        lpszClassName = name,
                    };
                    if (Native.RegisterClassExW(&wc) == 0)
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), $"RegisterClassEx({className})");
                    }
                }

                Registered.Add(className);
            }
        }

        var hwnd = Native.CreateWindowExW(exStyle, className, title, style, 0, 0, 0, 0, parent, 0, instance, null);
        if (hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CreateWindowEx({className})");
        }

        Handle = hwnd;
        lock (Gate)
        {
            Live[hwnd] = this;
        }
    }

    protected virtual nint WndProc(uint msg, nuint wParam, nint lParam) =>
        Native.DefWindowProcW(Handle, msg, wParam, lParam);

    [UnmanagedCallersOnly]
    private static nint WndProcThunk(nint hwnd, uint msg, nuint wParam, nint lParam)
    {
        NativeWindow? window;
        lock (Gate)
        {
            Live.TryGetValue(hwnd, out window);
        }

        if (window is null)
        {
            return Native.DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        try
        {
            return window.WndProc(msg, wParam, lParam);
        }
        catch (Exception e)
        {
            // Исключение не должно уйти в нативный код — это аварийное завершение процесса.
            Log.Error($"WndProc 0x{msg:X4}", e);
            return 0;
        }
    }
}

/// <summary>Окно с обработчиком-делегатом. null из обработчика — передать DefWindowProc.</summary>
internal sealed class MessageWindow : NativeWindow
{
    private readonly Func<uint, nuint, nint, nint?> _handler;

    public MessageWindow(string className, Func<uint, nuint, nint, nint?> handler, bool messageOnly)
    {
        _handler = handler;
        if (messageOnly)
        {
            CreateHandle(className, 0, 0, Native.HWND_MESSAGE);
        }
        else
        {
            // Скрытое окно верхнего уровня: никогда не показывается, но получает широковещательные сообщения.
            CreateHandle(className, Native.WS_EX_TOOLWINDOW, Native.WS_POPUP, 0, className);
        }
    }

    protected override nint WndProc(uint msg, nuint wParam, nint lParam) =>
        _handler(msg, wParam, lParam) ?? base.WndProc(msg, wParam, lParam);
}
