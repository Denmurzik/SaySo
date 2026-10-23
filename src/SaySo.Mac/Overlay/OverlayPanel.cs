using SaySo.Core.Diagnostics;
using SaySo.Core.Dictation;
using SaySo.Core.Overlay;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Overlay;

/// <summary>
/// Плашка поверх всех окон на macOS — та же v2, что в Windows: капсула выезжает пружиной из-за нижнего края экрана,
/// круг раздувается в капсулу, содержимое (столбики уровня, три точки, сообщение) сменяется растворением. Окно — NSPanel
/// без активации, пропускающая щелчки, на всех рабочих столах и поверх полноэкранных приложений; фон — размытие
/// NSVisualEffectView (как Acrylic), при «Уменьшить прозрачность» — сплошной, при «Уменьшить движение» — растворение
/// без выезда и морфинга. Окно и слои создаются при показе и освобождаются после скрытия; таймер кадров живёт, только
/// пока плашка видна. Только главный поток.
/// </summary>
internal sealed class OverlayPanel : IDictationOverlay, IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1.0 / 60);
    private static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(33);
    private static readonly TimeSpan MessageDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(220);

    private readonly Func<float> _level;
    private readonly float[] _levels = new float[OverlayMotion.BarCount];
    private readonly Fade[] _fades = [new(), new(), new()]; // Recording, Processing, Message
    private Scene? _scene;
    private OverlayMode _mode;
    private bool _visible;
    private bool _hiding;
    private double _progress;
    private double _revealFrom;
    private long _revealStart;
    private double _hideFrom;
    private long _hideStart;
    private double _widthFrom;
    private double _widthTo;
    private long _widthStart;
    private float _smoothed;
    private long _lastLevelTick;
    private IDisposable? _frameTimer;
    private IDisposable? _hideTimer;

    public OverlayPanel(Func<float> level)
    {
        _level = level;
    }

    /// <summary>Плашка скрылась и её окно освобождено — удобный момент вернуть память ОС.</summary>
    public event Action? Released;

    /// <summary>Есть ли сейчас окно плашки (для --overlay-demo: после скрытия его быть не должно).</summary>
    public bool HasScene => _scene is not null;

    public void ShowRecording()
    {
        _mode = OverlayMode.Recording;
        _smoothed = 0;
        Array.Clear(_levels);
        Present(scene =>
        {
            scene.SetLevels(_levels);
            return OverlayMotion.WidthPx(OverlayMotion.RecordingWidth, scene.Layout);
        });
    }

    public void ShowProcessing()
    {
        _mode = OverlayMode.Processing;
        Present(scene => OverlayMotion.WidthPx(OverlayMotion.RecordingWidth, scene.Layout));
    }

    public void ShowMessage(string text, OverlayTone tone)
    {
        _mode = OverlayMode.Message;
        if (!Present(scene => scene.SetMessage(text, tone)))
        {
            return;
        }

        _hideTimer = Dispatch.After(MessageDuration, () =>
        {
            if (_visible && !_hiding && _mode == OverlayMode.Message)
            {
                Hide();
            }
        });
    }

    /// <summary>Запускает анимацию скрытия; окно освобождается, когда она закончится.</summary>
    public void Hide()
    {
        StopHideTimer();
        if (!_visible || _hiding || _scene is null)
        {
            return;
        }

        _hiding = true;
        _hideFrom = _progress;
        _hideStart = Environment.TickCount64;
    }

    public void Dispose() => ReleaseScene(notify: false);

    /// <param name="fill">Кладёт содержимое в сцену и возвращает ширину капсулы.</param>
    /// <returns>false — окно создать не удалось, плашка не показана.</returns>
    private bool Present(Func<Scene, int> fill)
    {
        StopHideTimer();
        var fromHidden = !_visible;
        var reveal = !_visible || _hiding;
        var crossfade = _visible && !_hiding;
        var now = Environment.TickCount64;
        try
        {
            var scene = _scene ??= CreateScene();
            if (scene is null)
            {
                return false;
            }

            var width = fill(scene);
            SetContent(_mode, crossfade, now);
            if (fromHidden)
            {
                _widthFrom = _widthTo = width;
                _revealFrom = 0;
            }
            else
            {
                _widthFrom = CurrentWidth(now);
                _widthTo = width;
                _widthStart = now;
                if (reveal)
                {
                    _revealFrom = _progress; // плашка уезжала: возвращаем её с того места, где она сейчас
                }
            }

            if (reveal)
            {
                _revealStart = now;
                _hiding = false;
                _visible = true;
            }

            scene.Raise(); // и при каждом показе заново поверх окон, появившихся с прошлого раза
            _frameTimer ??= Dispatch.Every(FrameInterval, OnFrame);
            OnFrame();
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Плашка: показ", e);
            ReleaseScene(notify: true);
            return false;
        }
    }

    private Scene? CreateScene()
    {
        try
        {
            return Scene.Create();
        }
        catch (Exception e)
        {
            Log.Error("Плашка: окно", e);
            return null;
        }
    }

    private void SetContent(OverlayMode mode, bool crossfade, long now)
    {
        for (var i = 0; i < _fades.Length; i++)
        {
            var target = i == (int)mode ? 1.0 : 0.0;
            var fade = _fades[i];
            if (!crossfade)
            {
                fade.Set(target);
            }
            else if (target > 0)
            {
                fade.Start(fade.Value(now), 1, now + (long)OverlayMotion.ContentFadeInDelay.TotalMilliseconds, OverlayMotion.ContentFadeIn);
            }
            else
            {
                fade.Start(fade.Value(now), 0, now, OverlayMotion.ContentFadeOut);
            }
        }
    }

    private double CurrentWidth(long now)
    {
        var t = (now - _widthStart) / ResizeDuration.TotalMilliseconds;
        return _widthFrom + (_widthTo - _widthFrom) * OverlaySpring.EaseOut(t);
    }

    private void OnFrame()
    {
        if (_scene is not { } scene || !_visible)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (_hiding)
        {
            var hideSeconds = (now - _hideStart) / 1000.0;
            var done = scene.ReducedMotion
                ? hideSeconds >= OverlayMotion.ReducedMotionFade.TotalSeconds
                : hideSeconds >= OverlayMotion.HideDuration.TotalSeconds;
            if (done)
            {
                ReleaseScene(notify: true);
                return;
            }

            _progress = scene.ReducedMotion ? 1 : OverlaySpring.Hide(_hideFrom, hideSeconds);
        }
        else
        {
            _progress = scene.ReducedMotion ? 1 : OverlaySpring.Reveal(_revealFrom, (now - _revealStart) / 1000.0);
        }

        if (_mode == OverlayMode.Recording && now - _lastLevelTick >= LevelInterval.TotalMilliseconds)
        {
            _lastLevelTick = now;
            TickLevels(scene);
        }

        double windowAlpha = 1;
        if (scene.ReducedMotion)
        {
            var fade = OverlayMotion.ReducedMotionFade.TotalMilliseconds;
            windowAlpha = _hiding ? 1 - (now - _hideStart) / fade : (now - _revealStart) / fade;
        }

        var phase = now % (long)OverlayMotion.DotPeriod.TotalMilliseconds / OverlayMotion.DotPeriod.TotalMilliseconds;
        try
        {
            scene.Render(_progress, CurrentWidth(now), Math.Clamp(windowAlpha, 0, 1),
                _fades[0].Value(now), _fades[1].Value(now), _fades[2].Value(now), (float)phase);
        }
        catch (Exception e)
        {
            Log.Error("Плашка: кадр", e);
            ReleaseScene(notify: true);
        }
    }

    private void TickLevels(Scene scene)
    {
        var level = _level();
        // Быстрая атака, медленный спад — полоски «дышат», а не дёргаются (как в Windows).
        _smoothed = level > _smoothed ? level : _smoothed * 0.8f + level * 0.2f;
        OverlayMotion.PushLevelFromCenter(_levels, _smoothed);
        scene.SetLevels(_levels);
    }

    private void StopHideTimer()
    {
        _hideTimer?.Dispose();
        _hideTimer = null;
    }

    private void ReleaseScene(bool notify)
    {
        StopHideTimer();
        _frameTimer?.Dispose();
        _frameTimer = null;
        _visible = false;
        _hiding = false;
        _progress = 0;
        if (_scene is not { } scene)
        {
            return;
        }

        _scene = null;
        try
        {
            scene.Dispose();
        }
        catch (Exception e)
        {
            Log.Error("Плашка: освобождение", e);
        }

        if (notify)
        {
            Released?.Invoke();
        }
    }

    /// <summary>Растворение одной группы содержимого (столбики, точки, сообщение).</summary>
    private sealed class Fade
    {
        private double _from;
        private double _to;
        private long _start;
        private double _durationMs = 1;

        public void Set(double value)
        {
            _from = _to = value;
            _start = 0;
        }

        public void Start(double from, double to, long start, TimeSpan duration)
        {
            _from = from;
            _to = to;
            _start = start;
            _durationMs = Math.Max(1, duration.TotalMilliseconds);
        }

        public double Value(long now)
        {
            var t = (now - _start) / _durationMs;
            return t <= 0 ? _from : t >= 1 ? _to : _from + (_to - _from) * t;
        }
    }
}
