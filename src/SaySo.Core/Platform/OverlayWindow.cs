using System.Runtime.InteropServices;
using Microsoft.Win32;
using SaySo.Core.Dictation;
using SaySo.Core.Interop;
using SaySo.Core.Overlay;

namespace SaySo.Core.Platform;

/// <summary>
/// Плашка поверх всех окон (v2): капсула на Windows.UI.Composition с Acrylic, пружинным выездом из-за нижнего края экрана
/// и морфингом «круг → капсула». Окно не активируется и пропускает щелчки (WS_EX_LAYERED | WS_EX_TRANSPARENT), своего
/// растра у него нет (WS_EX_NOREDIRECTIONBITMAP): всё рисует DWM по дереву визуалов <see cref="OverlayScene"/>.
/// Сцена (очередь потока, компоновщик, визуалы) создаётся при показе и освобождается после анимации скрытия;
/// таймеры окна живут только пока плашка видна.
/// Стиль WS_EX_TOPMOST лишь сажает окно в верхнюю полосу, но не держит его выше других окон этой полосы:
/// созданное или активированное позже «всегда поверх» окно (плеер, Zoom, диспетчер задач) оказывается выше. Поэтому
/// каждый показ и каждый тик <see cref="TopmostTimer"/> заново ставят плашку на верх полосы без активации.
/// Выше полосы остаются окна оболочки (Пуск, всплывающие панели) и безопасный рабочий стол: без UIAccess их не перекрыть.
/// </summary>
internal sealed unsafe class OverlayWindow : NativeWindow, IDictationOverlay
{
    // Идентификаторы таймеров окна открыты тестам: KillTimer возвращает false, если таймера нет, — так проверяется «в простое таймеров нет».
    internal const nuint LevelTimer = 1;
    internal const nuint HideTimer = 2;
    internal const nuint TopmostTimer = 3;
    internal const nuint ReleaseTimer = 4;
    private const uint LevelMs = 33;
    private const uint TopmostMs = 250;
    private const uint MessageMs = 3000;
    private const uint ReleaseMs = 1500; // страховка: если DWM не сообщит о конце скрытия, плашка всё равно освободится
    private const uint WM_SCENE_HIDDEN = Native.WM_APP + 1;

    private readonly Func<float> _level;
    private readonly float[] _levels = new float[OverlayMotion.BarCount];
    private readonly bool _hostBackdropAccepted;
    private OverlayScene? _scene;
    private OverlayMode _mode;
    private bool _visible;
    private bool _hiding;
    private float _smoothed;
    private long _hideAt;
    private bool _raiseFailureLogged;
    private bool _sceneFailureLogged;

    public OverlayWindow(Func<float> level)
    {
        _level = level;
        CreateHandle("SaySo.Overlay",
            Native.WS_EX_LAYERED | Native.WS_EX_TOPMOST | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT
            | Native.WS_EX_NOREDIRECTIONBITMAP,
            Native.WS_POPUP, 0);

        // Слоистое окно с постоянной альфой 255: щелчки проходят насквозь, а содержимое целиком рисует компоновщик.
        if (!Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA))
        {
            PlatformLog.Info($"SetLayeredWindowAttributes не удался: код {Marshal.GetLastPInvokeError()}");
        }

        // До первого показа: окно разрешает себе HostBackdropBrush (Windows 11). На Windows 10 вернёт ошибку — сплошной фон.
        var on = 1;
        _hostBackdropAccepted = Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_HOSTBACKDROPBRUSH, &on, sizeof(int)) >= 0;
    }

    /// <summary>Плашка скрылась и сцена освобождена — удобный момент вернуть память ОС.</summary>
    public event Action? Released;

    /// <summary>Есть ли сейчас сцена компоновщика (для тестов: после скрытия её быть не должно).</summary>
    internal bool HasScene => _scene is not null;

    /// <summary>Раскладка текущей сцены (для тестов: где на экране капсула).</summary>
    internal OverlayLayout? Layout => _scene?.Layout;

    /// <summary>Материал текущей сцены (для тестов).</summary>
    internal OverlayMaterial? Material => _scene?.Material;

    /// <summary>Идёт ли текущая сцена в режиме «Уменьшение движения» (для тестов).</summary>
    internal bool? SceneReducedMotion => _scene?.ReducedMotion;

    /// <summary>Подмена системных условий в тестах: выбор материала и «Уменьшение движения».</summary>
    internal Func<OverlayMaterial>? MaterialOverride { get; set; }

    internal Func<bool>? ReducedMotionOverride { get; set; }

    public void ShowRecording()
    {
        _mode = OverlayMode.Recording;
        _smoothed = 0;
        Array.Clear(_levels);
        var crossfade = _visible && !_hiding;
        if (Present(scene => { scene.ShowBars(_levels, crossfade); return OverlayMotion.WidthPx(OverlayMotion.RecordingWidth, scene.Layout); }))
        {
            Native.SetTimer(Handle, LevelTimer, LevelMs, null);
        }
    }

    public void ShowProcessing()
    {
        _mode = OverlayMode.Processing;
        var crossfade = _visible && !_hiding;
        Present(scene => { scene.ShowDots(crossfade); return OverlayMotion.WidthPx(OverlayMotion.RecordingWidth, scene.Layout); });
    }

    public void ShowMessage(string text, OverlayTone tone)
    {
        _mode = OverlayMode.Message;
        var crossfade = _visible && !_hiding;
        if (!Present(scene => scene.ShowMessage(text, tone, crossfade)))
        {
            return; // не показали — и таймер скрытия не нужен
        }

        // Допуск в полпериода: тик настоящего таймера приходит с погрешностью ~15 мс, а устаревший — заведомо раньше.
        _hideAt = Environment.TickCount64 + MessageMs / 2;
        Native.SetTimer(Handle, HideTimer, MessageMs, null);
    }

    /// <summary>Запускает анимацию скрытия; сцена освобождается, когда она закончится.</summary>
    public void Hide()
    {
        StopTimers();
        if (!_visible || _hiding || _scene is not { } scene)
        {
            return;
        }

        _hiding = true;
        try
        {
            scene.Conceal(() => Native.PostMessageW(Handle, WM_SCENE_HIDDEN, 0, 0));
            Native.SetTimer(Handle, ReleaseTimer, ReleaseMs, null);
        }
        catch (Exception e)
        {
            PlatformLog.Error("Плашка: анимация скрытия", e);
            ReleaseScene();
        }
    }

    public override void Dispose()
    {
        ReleaseScene(notify: false);
        base.Dispose();
    }

    protected override nint WndProc(uint msg, nuint wParam, nint lParam)
    {
        switch (msg)
        {
            // KillTimer не убирает уже поставленный в очередь WM_TIMER: устаревшие тики игнорируем.
            case Native.WM_TIMER when wParam == LevelTimer:
                if (_visible && !_hiding && _mode == OverlayMode.Recording)
                {
                    Tick();
                }

                return 0;
            case Native.WM_TIMER when wParam == TopmostTimer:
                if (_visible && !_hiding)
                {
                    RaiseToTop();
                }

                return 0;
            case Native.WM_TIMER when wParam == HideTimer:
                if (_visible && !_hiding && _mode == OverlayMode.Message && Environment.TickCount64 >= _hideAt)
                {
                    Hide();
                }

                return 0;
            case Native.WM_TIMER when wParam == ReleaseTimer:
                if (_hiding)
                {
                    ReleaseScene();
                }

                return 0;
            case WM_SCENE_HIDDEN:
                if (_hiding)
                {
                    ReleaseScene(); // вне обратного вызова очереди компоновщика: здесь её можно закрыть
                }

                return 0;
            case Native.WM_SETTINGCHANGE when _scene is not null && lParam != 0
                                              && new string((char*)lParam) == "ImmersiveColorSet":
                ApplyMaterial(); // переключили «Эффекты прозрачности» или тему, пока плашка видна
                break;
        }

        return base.WndProc(msg, wParam, lParam);
    }

    /// <param name="fill">Кладёт содержимое в сцену и возвращает ширину капсулы.</param>
    /// <returns>false — сцену создать не удалось, плашка не показана.</returns>
    private bool Present(Func<OverlayScene, int> fill)
    {
        Native.KillTimer(Handle, HideTimer);
        Native.KillTimer(Handle, LevelTimer);
        var fromHidden = !_visible;
        var reveal = !_visible || _hiding;
        try
        {
            var scene = _scene ??= CreateScene();
            if (scene is null)
            {
                return false;
            }

            var width = fill(scene);
            if (reveal)
            {
                scene.Reveal(width, fromHidden);
            }
            else
            {
                scene.Resize(width);
            }
        }
        catch (Exception e)
        {
            PlatformLog.Error("Плашка: показ", e);
            ReleaseScene(); // сцена могла успеть создаться: демон всё равно сбросит память
            return false;
        }

        if (fromHidden)
        {
            var layout = _scene!.Layout;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, layout.WindowLeft, layout.WindowTop, layout.WindowWidth, layout.WindowHeight,
                Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            _raiseFailureLogged = false;
        }

        if (reveal)
        {
            _visible = true;
            _hiding = false;
            Native.KillTimer(Handle, ReleaseTimer);
            Native.SetTimer(Handle, TopmostTimer, TopmostMs, null);
        }

        RaiseToTop(); // показывает окно и в каждом режиме заново ставит его над окнами, появившимися с прошлого показа
        return true;
    }

    private OverlayScene? CreateScene()
    {
        var layout = CurrentLayout();
        try
        {
            var scene = new OverlayScene(Handle, layout, ChooseMaterial(), ReducedMotionOverride?.Invoke() ?? ReducedMotion());
            _sceneFailureLogged = false;
            return scene;
        }
        catch (Exception e)
        {
            if (!_sceneFailureLogged)
            {
                _sceneFailureLogged = true; // раз до первого удачного показа: сбой, скорее всего, повторится
                PlatformLog.Error("Плашка: компоновщик", e);
            }

            return null;
        }
    }

    private void ApplyMaterial()
    {
        try
        {
            _scene?.SetMaterial(ChooseMaterial());
        }
        catch (Exception e)
        {
            PlatformLog.Error("Плашка: смена материала", e);
        }
    }

    private OverlayMaterial ChooseMaterial()
    {
        if (MaterialOverride is { } chosen)
        {
            return chosen();
        }

        Native.SYSTEM_POWER_STATUS power;
        var batterySaver = Native.GetSystemPowerStatus(&power) && (power.SystemStatusFlag & 1) != 0;
        return OverlayMotion.ChooseMaterial(Environment.OSVersion.Version.Build, TransparencyEffectsEnabled(), batterySaver, _hostBackdropAccepted);
    }

    private static bool TransparencyEffectsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int value || value != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>«Показывать анимацию в Windows» выключено — плашка растворяется без выезда и морфинга.</summary>
    private static bool ReducedMotion()
    {
        var animate = 1;
        return Native.SystemParametersInfoW(Native.SPI_GETCLIENTAREAANIMATION, 0, &animate, 0) && animate == 0;
    }

    /// <summary>Освобождает сцену и прячет окно; вызывается по окончании анимации скрытия (или при сбое).</summary>
    private void ReleaseScene(bool notify = true)
    {
        StopTimers();
        Native.KillTimer(Handle, ReleaseTimer);
        var wasLive = _scene is not null;
        if (Handle != 0 && _visible)
        {
            Native.ShowWindow(Handle, Native.SW_HIDE);
        }

        _visible = false;
        _hiding = false;
        if (_scene is { } scene)
        {
            _scene = null;
            try
            {
                scene.Dispose();
            }
            catch (Exception e)
            {
                PlatformLog.Error("Плашка: освобождение", e);
            }
        }

        if (notify && wasLive)
        {
            Released?.Invoke();
        }
    }

    private void StopTimers()
    {
        if (Handle == 0)
        {
            return;
        }

        Native.KillTimer(Handle, LevelTimer);
        Native.KillTimer(Handle, HideTimer);
        Native.KillTimer(Handle, TopmostTimer);
    }

    /// <summary>Показывает плашку (если скрыта) и ставит её на верх полосы «поверх всех окон»; фокус не берёт.</summary>
    private void RaiseToTop()
    {
        const uint Flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_SHOWWINDOW;
        if (!Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Flags) && !_raiseFailureLogged)
        {
            _raiseFailureLogged = true; // раз за показ: подъём идёт каждые 250 мс
            PlatformLog.Info($"SetWindowPos(HWND_TOPMOST) не удался: код {Marshal.GetLastPInvokeError()}");
        }
    }

    private void Tick()
    {
        var level = _level();
        // Быстрая атака, медленный спад — полоски «дышат», а не дёргаются.
        _smoothed = level > _smoothed ? level : _smoothed * 0.8f + level * 0.2f;
        OverlayMotion.PushLevelFromCenter(_levels, _smoothed);
        try
        {
            _scene?.SetLevels(_levels);
        }
        catch (Exception e)
        {
            PlatformLog.Error("Плашка: уровень", e);
            Hide();
        }
    }

    /// <summary>Монитор активного окна: плашка внизу по центру его рабочей области.</summary>
    private static OverlayLayout CurrentLayout()
    {
        var monitor = Native.MonitorFromWindow(Native.GetForegroundWindow(), Native.MONITOR_DEFAULTTOPRIMARY);
        var info = new Native.MONITORINFO { cbSize = (uint)sizeof(Native.MONITORINFO) };
        Native.GetMonitorInfoW(monitor, &info);
        uint dpiX, dpiY;
        var scale = Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, &dpiX, &dpiY) == 0 ? dpiX / 96f : 1f;
        return OverlayMotion.Layout(scale, info.rcMonitor.Bottom, info.rcWork.Left, info.rcWork.Right, info.rcWork.Bottom);
    }
}
