using SaySo.Core.Dictation;
using SaySo.Core.Interop;
using SaySo.Core.Overlay;

namespace SaySo.Core.Platform;

/// <summary>
/// Плашка поверх всех окон: не активируется, пропускает щелчки, рисуется через UpdateLayeredWindow
/// с попиксельной альфой. Таймеры (анимации и подъёма наверх) живут только пока плашка видна.
/// Стиль WS_EX_TOPMOST лишь сажает окно в верхнюю полосу, но не держит его выше других окон этой полосы:
/// созданное или активированное позже «всегда поверх» окно (плеер, Zoom, диспетчер задач) оказывается выше. Поэтому
/// каждый показ и каждый тик <see cref="TopmostTimer"/> заново ставят плашку на верх полосы без активации.
/// Выше полосы остаются окна оболочки (Пуск, всплывающие панели) и безопасный рабочий стол: без UIAccess их не перекрыть.
/// </summary>
internal sealed unsafe class OverlayWindow : NativeWindow, IDictationOverlay
{
    private const nuint AnimationTimer = 1;
    private const nuint HideTimer = 2;
    private const nuint TopmostTimer = 3;
    private const uint FrameMs = 33;
    private const uint TopmostMs = 250; // у сообщения нет таймера анимации, поэтому подъём — отдельный и редкий
    private const uint MessageMs = 3000;
    private const float BottomMargin = 48f;

    private readonly Func<float> _level;
    private readonly float[] _levels = new float[OverlayRenderer.BarCount];
    private readonly long _epoch = Environment.TickCount64;
    private OverlayMode _mode;
    private OverlayTone _tone;
    private TextMask? _text;
    private bool _visible;
    private float _smoothed;
    private long _hideAt;
    private bool _layerFailureLogged;
    private bool _raiseFailureLogged;
    private nint _memDc;
    private nint _dib;
    private uint* _bits;
    private int _dibWidth;
    private int _dibHeight;

    public OverlayWindow(Func<float> level)
    {
        _level = level;
        CreateHandle("SaySo.Overlay",
            Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT,
            Native.WS_POPUP, 0);
    }

    public void ShowRecording()
    {
        _mode = OverlayMode.Recording;
        _text = null;
        _smoothed = 0;
        Array.Clear(_levels);
        Present(animate: true);
    }

    public void ShowProcessing()
    {
        _mode = OverlayMode.Processing;
        _text = null;
        Present(animate: true);
    }

    public void ShowMessage(string text, OverlayTone tone)
    {
        _mode = OverlayMode.Message;
        _tone = tone;
        var scale = CurrentScale(out _);
        // Ширина текста не больше 480·scale − 48·scale: тогда многоточие ставит GDI, а не жёсткая обрезка рендерера.
        // Пустая маска (сбой GDI) рендерер трактует как «без текста».
        _text = GdiTextMask.RenderText(text, (int)MathF.Round(13 * scale), (int)MathF.Round(400 * scale));
        if (!Present(animate: false))
        {
            return; // не показали — и таймер скрытия не нужен
        }

        // Допуск в полпериода: тик настоящего таймера приходит с погрешностью ~15 мс, а устаревший — заведомо раньше.
        _hideAt = Environment.TickCount64 + MessageMs / 2;
        Native.SetTimer(Handle, HideTimer, MessageMs, null);
    }

    public void Hide()
    {
        if (Handle != 0)
        {
            Native.KillTimer(Handle, AnimationTimer);
            Native.KillTimer(Handle, HideTimer);
            Native.KillTimer(Handle, TopmostTimer);
            if (_visible)
            {
                Native.ShowWindow(Handle, Native.SW_HIDE);
            }
        }

        _visible = false;
        _text = null;
        FreeBitmap(); // в простое не держим даже маленький DIB
    }

    public override void Dispose()
    {
        Hide();
        base.Dispose();
    }

    protected override nint WndProc(uint msg, nuint wParam, nint lParam)
    {
        if (msg == Native.WM_TIMER)
        {
            // KillTimer не убирает уже поставленный в очередь WM_TIMER: устаревшие тики игнорируем.
            if (wParam == AnimationTimer)
            {
                if (_visible)
                {
                    Tick();
                }

                return 0;
            }

            if (wParam == TopmostTimer)
            {
                if (_visible)
                {
                    RaiseToTop();
                }

                return 0;
            }

            if (wParam == HideTimer)
            {
                if (_visible && _mode == OverlayMode.Message && Environment.TickCount64 >= _hideAt)
                {
                    Hide();
                }

                return 0;
            }
        }

        return base.WndProc(msg, wParam, lParam);
    }

    /// <returns>false — плашку нарисовать не удалось, она не показана.</returns>
    private bool Present(bool animate)
    {
        Native.KillTimer(Handle, HideTimer);
        _layerFailureLogged = false;
        if (!Draw())
        {
            Hide(); // таймер анимации не должен пережить неудачный кадр
            return false;
        }

        if (!_visible)
        {
            _visible = true;
            _raiseFailureLogged = false;
            Native.SetTimer(Handle, TopmostTimer, TopmostMs, null);
        }

        RaiseToTop(); // показывает окно и в каждом режиме заново ставит его над окнами, появившимися с прошлого показа

        if (animate)
        {
            Native.SetTimer(Handle, AnimationTimer, FrameMs, null);
        }
        else
        {
            Native.KillTimer(Handle, AnimationTimer);
        }

        return true;
    }

    /// <summary>Показывает плашку (если скрыта) и ставит её на верх полосы «поверх всех окон»; фокус не берёт.</summary>
    private void RaiseToTop()
    {
        const uint Flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW;
        if (!Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Flags) && !_raiseFailureLogged)
        {
            _raiseFailureLogged = true; // раз за показ: подъём идёт каждые 250 мс
            PlatformLog.Info($"SetWindowPos(HWND_TOPMOST) не удался: код {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }
    }

    private void Tick()
    {
        if (_mode == OverlayMode.Recording)
        {
            var level = _level();
            // Быстрая атака, медленный спад — полоски «дышат», а не дёргаются.
            _smoothed = level > _smoothed ? level : _smoothed * 0.8f + level * 0.2f;
            Array.Copy(_levels, 1, _levels, 0, _levels.Length - 1);
            _levels[^1] = _smoothed;
        }

        if (!Draw())
        {
            Hide();
        }
    }

    /// <returns>false — не удалось создать DIB, плашка не нарисована.</returns>
    private bool Draw()
    {
        var scale = CurrentScale(out var work);
        var frame = new OverlayFrame(_mode, _levels, Environment.TickCount64 - _epoch, _tone, _text);
        var (w, h) = OverlayRenderer.Measure(frame, scale);
        if (!EnsureBitmap(w, h))
        {
            return false;
        }

        OverlayRenderer.Render(new Span<uint>(_bits, w * h), w, h, frame, scale);

        var position = new Native.POINT
        {
            X = work.Left + (work.Right - work.Left - w) / 2,
            Y = work.Bottom - h - (int)MathF.Round(BottomMargin * scale),
        };
        var size = new Native.SIZE { cx = w, cy = h };
        var source = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
        if (!Native.UpdateLayeredWindow(Handle, 0, &position, &size, _memDc, &source, 0, &blend, Native.ULW_ALPHA)
            && !_layerFailureLogged)
        {
            _layerFailureLogged = true; // раз за показ: кадры идут каждые 33 мс
            PlatformLog.Info($"UpdateLayeredWindow не удался: код {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
        }

        return true;
    }

    private static float CurrentScale(out Native.RECT work)
    {
        var monitor = Native.MonitorFromWindow(Native.GetForegroundWindow(), Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new Native.MONITORINFO { cbSize = (uint)sizeof(Native.MONITORINFO) };
        Native.GetMonitorInfoW(monitor, &info);
        work = info.rcWork;
        uint dpiX, dpiY;
        return Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, &dpiX, &dpiY) == 0 ? dpiX / 96f : 1f;
    }

    private bool EnsureBitmap(int w, int h)
    {
        if (_dib != 0 && w == _dibWidth && h == _dibHeight)
        {
            return true;
        }

        FreeBitmap();
        _memDc = Native.CreateCompatibleDC(0);
        var info = Native.TopDown32(w, h);
        void* bits = null;
        _dib = _memDc != 0 ? Native.CreateDIBSection(_memDc, &info, 0, &bits, 0, 0) : 0;
        if (_dib == 0 || bits == null)
        {
            FreeBitmap();
            return false;
        }

        _bits = (uint*)bits;
        Native.SelectObject(_memDc, _dib);
        _dibWidth = w;
        _dibHeight = h;
        return true;
    }

    private void FreeBitmap()
    {
        if (_memDc != 0)
        {
            Native.DeleteDC(_memDc);
            _memDc = 0;
        }

        if (_dib != 0)
        {
            Native.DeleteObject(_dib);
            _dib = 0;
        }

        _bits = null;
        _dibWidth = _dibHeight = 0;
    }
}
