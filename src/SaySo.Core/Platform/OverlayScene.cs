using System.Numerics;
using SaySo.Core.Interop;
using SaySo.Core.Overlay;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32.Graphics.Direct2D;

namespace SaySo.Core.Platform;

/// <summary>
/// Дерево Windows.UI.Composition плашки: очередь потока, компоновщик, цель окна и капсула. Создаётся при показе и
/// освобождается целиком после анимации скрытия — в простое в процессе не остаётся ни компоновщика, ни очереди.
/// Анимации выполняет DWM с частотой монитора; наш поток только задаёт целевые значения.
/// </summary>
/// <remarks>
/// Устройство: root → capsule (Offset.Y из Progress) → body (клип-капсула с отступом 1 px: материал и содержимое) + рамка.
/// Рамка — внешний пиксель капсулы, как у окон Windows 11 (#66757575 поверх рабочего стола).
/// </remarks>
internal sealed unsafe class OverlayScene : IDisposable
{
    private static readonly Color Surface = Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C);
    private const float LuminosityAlpha = 0.88f; // слой «светимости» (рецепт Acrylic без режима Luminosity — приближение слоями)
    private const float TintAlpha = 0.15f;
    private const float SolidAlpha = 0.96f;
    private static readonly Color Border = Color.FromArgb(0x66, 0x75, 0x75, 0x75); // SurfaceStrokeColorDefault, снят с рамки окна
    private static readonly Color Ink = Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF);
    private static readonly Color ErrorTone = Color.FromArgb(0xFF, 0xE8, 0x4B, 0x4B);
    private static readonly Color InfoTone = Color.FromArgb(0xFF, 0xF2, 0xB3, 0x3D);

    private readonly OverlayLayout _layout;
    private readonly bool _reducedMotion;
    private Windows.System.DispatcherQueueController? _queue;
    private Compositor? _compositor;
    private DesktopWindowTarget? _target;
    private ContainerVisual? _root;
    private ContainerVisual? _capsule;
    private ContainerVisual? _material;
    private ContainerVisual? _content;
    private CompositionPropertySet? _p;
    private ShapeVisual? _bars;
    private CompositionRoundedRectangleGeometry[] _barGeometries = [];
    private Visual? _page;
    private nint _d2d;
    private int _hideGeneration;

    /// <exception cref="Exception">Компоновщик или цель окна не созданы (вызывающий пишет в журнал и не показывает плашку).</exception>
    public OverlayScene(nint hwnd, OverlayLayout layout, OverlayMaterial material, bool reducedMotion)
    {
        _layout = layout;
        _reducedMotion = reducedMotion;
        try
        {
            CreateQueue();
            _compositor = new Compositor();
            _target = CreateTarget(_compositor, hwnd);
            Build(material);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public OverlayLayout Layout => _layout;

    public OverlayMaterial Material { get; private set; }

    public bool ReducedMotion => _reducedMotion;

    private Compositor C => _compositor ?? throw new ObjectDisposedException(nameof(OverlayScene));

    private void CreateQueue()
    {
        // Компоновщик требует DispatcherQueue на своём потоке; очередь живёт только вместе со сценой.
        var options = new Native.DispatcherQueueOptions
        {
            dwSize = (uint)sizeof(Native.DispatcherQueueOptions),
            threadType = 2,    // DQTYPE_THREAD_CURRENT
            apartmentType = 0, // DQTAT_COM_NONE: для текущего потока COM не инициализируется (поле — для выделенного потока); поток демона и так STA
        };
        nint controller;
        var hr = Native.CreateDispatcherQueueController(options, &controller);
        if (hr < 0)
        {
            throw new InvalidOperationException($"CreateDispatcherQueueController: 0x{hr:X8}");
        }

        _queue = Windows.System.DispatcherQueueController.FromAbi(controller);
        Native.Release(controller);
    }

    private static DesktopWindowTarget CreateTarget(Compositor compositor, nint hwnd)
    {
        var unknown = WinRT.MarshalInspectable<Compositor>.FromManaged(compositor);
        try
        {
            var hr = Native.QueryInterface(unknown, Native.IID_ICompositorDesktopInterop, out var interop);
            if (hr < 0)
            {
                throw new InvalidOperationException($"ICompositorDesktopInterop: 0x{hr:X8}");
            }

            try
            {
                hr = Native.CreateDesktopWindowTarget(interop, hwnd, topmost: true, out var target);
                if (hr < 0)
                {
                    throw new InvalidOperationException($"CreateDesktopWindowTarget: 0x{hr:X8}");
                }

                try
                {
                    return DesktopWindowTarget.FromAbi(target);
                }
                finally
                {
                    Native.Release(target);
                }
            }
            finally
            {
                Native.Release(interop);
            }
        }
        finally
        {
            Native.Release(unknown);
        }
    }

    private void Build(OverlayMaterial material)
    {
        var c = C;
        var height = _layout.CapsuleHeight;
        var box = new Vector2(_layout.CapsuleMaxWidth, height);

        _p = c.CreatePropertySet();
        _p.InsertScalar("Progress", 0f);
        _p.InsertScalar("Width", height);
        _p.InsertScalar("Height", height);
        _p.InsertScalar("MaxWidth", _layout.CapsuleMaxWidth);
        _p.InsertScalar("StartY", _layout.StartY);
        _p.InsertScalar("EndY", _layout.EndY);

        _root = c.CreateContainerVisual();
        _root.Size = new Vector2(_layout.WindowWidth, _layout.WindowHeight);
        _target!.Root = _root;

        _capsule = c.CreateContainerVisual();
        _capsule.Size = box;
        _capsule.Offset = new Vector3(_layout.CapsuleLeft, _layout.StartY, 0);
        _root.Children.InsertAtTop(_capsule);

        // Тело: клип-капсула на 1 px внутри края — внешний пиксель занят рамкой.
        var body = c.CreateContainerVisual();
        body.Size = box;
        var clip = c.CreateRoundedRectangleGeometry();
        clip.CornerRadius = new Vector2((height - 2) / 2f);
        body.Clip = c.CreateGeometricClip(clip);
        _capsule.Children.InsertAtTop(body);

        _material = c.CreateContainerVisual();
        _material.Size = box;
        body.Children.InsertAtTop(_material);
        SetMaterial(material);

        _content = c.CreateContainerVisual();
        _content.Size = box;
        body.Children.InsertAtTop(_content);

        var frame = c.CreateShapeVisual();
        frame.Size = box;
        var frameGeometry = c.CreateRoundedRectangleGeometry();
        frameGeometry.CornerRadius = new Vector2((height - 1) / 2f);
        var stroke = c.CreateSpriteShape(frameGeometry);
        stroke.StrokeBrush = c.CreateColorBrush(Border);
        stroke.StrokeThickness = 1f; // визуалы цели окна — в физических пикселях: ровно 1 px при любом DPI
        frame.Shapes.Add(stroke);
        _capsule.Children.InsertAtTop(frame);

        Bind(_capsule, "Offset.Y", OverlayMotion.Expressions.Y);
        Bind(clip, "Size", OverlayMotion.Expressions.ClipSize);
        Bind(clip, "Offset", OverlayMotion.Expressions.ClipOffset);
        Bind(frameGeometry, "Size", OverlayMotion.Expressions.BorderSize);
        Bind(frameGeometry, "Offset", OverlayMotion.Expressions.BorderOffset);
        Bind(_content, "Opacity", OverlayMotion.Expressions.ContentOpacity);
    }

    private void Bind(CompositionObject target, string property, string expression)
    {
        var animation = C.CreateExpressionAnimation(expression);
        animation.SetReferenceParameter("p", _p!);
        target.StartAnimation(property, animation);
    }

    /// <summary>Материал капсулы; меняется и на лету (WM_SETTINGCHANGE «ImmersiveColorSet»).</summary>
    public void SetMaterial(OverlayMaterial material)
    {
        var c = C;
        Material = material;
        _material!.Children.RemoveAll();
        if (material == OverlayMaterial.Acrylic)
        {
            // Размытие того, что под окном (DWMWA_USE_HOSTBACKDROPBRUSH), затем тонировка по рецепту WinUI (тёмная тема).
            AddLayer(c.CreateHostBackdropBrush());
            AddLayer(c.CreateColorBrush(WithAlpha(Surface, LuminosityAlpha)));
            AddLayer(c.CreateColorBrush(WithAlpha(Surface, TintAlpha)));
        }
        else
        {
            AddLayer(c.CreateColorBrush(WithAlpha(Surface, SolidAlpha)));
        }

        void AddLayer(CompositionBrush brush)
        {
            var sprite = c.CreateSpriteVisual();
            sprite.Size = _material.Size;
            sprite.Brush = brush;
            _material.Children.InsertAtTop(sprite);
        }
    }

    // ---------- содержимое ----------

    /// <param name="crossfade">false — плашка ещё не видна: содержимое проявит сам выезд (прозрачность от Progress).</param>
    public void ShowBars(ReadOnlySpan<float> levels, bool crossfade)
    {
        if (_bars is null)
        {
            var c = C;
            _bars = c.CreateShapeVisual();
            _bars.Size = new Vector2(_layout.CapsuleMaxWidth, _layout.CapsuleHeight);
            var brush = c.CreateColorBrush(Ink);
            _barGeometries = new CompositionRoundedRectangleGeometry[OverlayMotion.BarCount];
            var radius = OverlayMotion.Px(OverlayMotion.BarWidth, _layout.Scale) / 2f; // концы скруглены полностью
            for (var i = 0; i < _barGeometries.Length; i++)
            {
                _barGeometries[i] = c.CreateRoundedRectangleGeometry();
                _barGeometries[i].CornerRadius = new Vector2(radius);
                var shape = c.CreateSpriteShape(_barGeometries[i]);
                shape.FillBrush = brush;
                _bars.Shapes.Add(shape);
            }
        }

        SetLevels(levels);
        SwitchTo(_bars, crossfade);
    }

    public void SetLevels(ReadOnlySpan<float> levels)
    {
        if (_bars is null)
        {
            return;
        }

        var bars = OverlayMotion.Bars(levels, _layout);
        for (var i = 0; i < bars.Length; i++)
        {
            _barGeometries[i].Size = new Vector2(bars[i].Width, bars[i].Height);
            _barGeometries[i].Offset = new Vector2(bars[i].X, bars[i].Y);
        }
    }

    public void ShowDots(bool crossfade)
    {
        var c = C;
        var page = c.CreateContainerVisual();
        page.Size = new Vector2(_layout.CapsuleMaxWidth, _layout.CapsuleHeight);
        var brush = c.CreateColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        var radius = OverlayMotion.DotRadius * _layout.Scale;
        var centers = OverlayMotion.DotCenters(_layout);
        for (var i = 0; i < centers.Length; i++)
        {
            var dot = c.CreateShapeVisual();
            dot.Size = new Vector2(2 * radius + 2);
            dot.Offset = new Vector3(centers[i].X - radius - 1, centers[i].Y - radius - 1, 0);
            var circle = c.CreateEllipseGeometry();
            circle.Center = new Vector2(radius + 1);
            circle.Radius = new Vector2(radius);
            var shape = c.CreateSpriteShape(circle);
            shape.FillBrush = brush;
            dot.Shapes.Add(shape);

            // Пульсация — анимацией компоновщика: таймер окна для точек не нужен.
            var pulse = c.CreateScalarKeyFrameAnimation();
            var linear = c.CreateLinearEasingFunction();
            const int Steps = 8;
            for (var k = 0; k <= Steps; k++)
            {
                pulse.InsertKeyFrame(k / (float)Steps, OverlayMotion.DotOpacity(i, k / (float)Steps), linear);
            }

            pulse.Duration = OverlayMotion.DotPeriod;
            pulse.IterationBehavior = AnimationIterationBehavior.Forever;
            dot.StartAnimation("Opacity", pulse);
            page.Children.InsertAtTop(dot);
        }

        SwitchTo(page, crossfade);
    }

    /// <returns>Ширина капсулы для сообщения (пиксели).</returns>
    public int ShowMessage(string text, OverlayTone tone, bool crossfade)
    {
        var c = C;
        var page = c.CreateContainerVisual();
        page.Size = new Vector2(_layout.CapsuleMaxWidth, _layout.CapsuleHeight);

        var fontPx = OverlayMotion.Px(OverlayMotion.FontSize, _layout.Scale);
        var maxText = OverlayMotion.MaxTextWidth * _layout.Scale;
        var glyphs = BuildText(text, fontPx, maxText, out var textWidth);
        var width = OverlayMotion.MessageWidthPx(textWidth, _layout);
        var parts = OverlayMotion.MessageParts(width, _layout);

        var dot = c.CreateShapeVisual();
        dot.Size = page.Size;
        var circle = c.CreateEllipseGeometry();
        circle.Center = new Vector2(parts.DotX, parts.DotY);
        circle.Radius = new Vector2(parts.DotRadius);
        var dotShape = c.CreateSpriteShape(circle);
        dotShape.FillBrush = c.CreateColorBrush(tone switch
        {
            OverlayTone.Error => ErrorTone,
            OverlayTone.Info => InfoTone,
            _ => Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        });
        dot.Shapes.Add(dotShape);
        page.Children.InsertAtTop(dot);

        if (glyphs is not null)
        {
            var label = c.CreateShapeVisual();
            label.Size = new Vector2(Math.Max(1f, _layout.CapsuleMaxWidth - parts.TextLeft), _layout.CapsuleHeight);
            label.Offset = new Vector3(parts.TextLeft, 0, 0);
            var shape = c.CreateSpriteShape(c.CreatePathGeometry(glyphs));
            shape.FillBrush = c.CreateColorBrush(Ink);
            label.Shapes.Add(shape);
            page.Children.InsertAtTop(label);
        }

        SwitchTo(page, crossfade);
        return width;
    }

    /// <summary>Путь текста для компоновщика; null — текст не построился (сообщение покажется с одной точкой тона).</summary>
    private CompositionPath? BuildText(string text, int fontPx, float maxWidth, out float width)
    {
        width = 0;
        if (_d2d == 0)
        {
            var iid = ID2D1Factory.IID_Guid;
            void* factory;
            // Многопоточная фабрика: пути освобождает и поток финализатора (GeometrySource — IAgileObject, его держит обёртка CsWinRT).
            if (Windows.Win32.Win32.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, &iid, null, &factory) < 0)
            {
                PlatformLog.Info("D2D1CreateFactory не удался: текст плашки не нарисован");
                return null;
            }

            _d2d = (nint)factory;
        }

        var geometry = GlyphText.Build((ID2D1Factory*)_d2d, text, fontPx, maxWidth, _layout.CapsuleHeight / 2f, out width);
        if (geometry is null)
        {
            PlatformLog.Info("Контуры текста плашки не получены (сбой GDI)");
            return null;
        }

        var source = GeometrySource.Create((nint)geometry);
        Native.Release((nint)geometry);
        try
        {
            return new CompositionPath(WinRT.MarshalInterface<Windows.Graphics.IGeometrySource2D>.FromAbi(source));
        }
        finally
        {
            Native.Release(source);
        }
    }

    /// <summary>Новое содержимое проявляется, прежнее гаснет и удаляется из дерева.</summary>
    private void SwitchTo(Visual page, bool crossfade)
    {
        var c = C;
        var content = _content!;
        if (ReferenceEquals(page, _page))
        {
            return;
        }

        var old = _page;
        _page = page;
        if (page.Parent is null)
        {
            content.Children.InsertAtTop(page);
        }

        page.StopAnimation("Opacity"); // столбики могли ещё догасать с прошлого раза
        if (!crossfade || _reducedMotion)
        {
            page.Opacity = 1f;
            if (old is not null)
            {
                Remove(old);
            }

            return;
        }

        page.Opacity = 0f;
        var fadeIn = c.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(1f, 1f);
        fadeIn.Duration = OverlayMotion.ContentFadeIn;
        fadeIn.DelayTime = OverlayMotion.ContentFadeInDelay;
        page.StartAnimation("Opacity", fadeIn);

        if (old is null)
        {
            return;
        }

        var fadeOut = c.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(1f, 0f);
        fadeOut.Duration = OverlayMotion.ContentFadeOut;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        old.StartAnimation("Opacity", fadeOut);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (_compositor is not null && !ReferenceEquals(old, _page))
            {
                Remove(old);
            }
        };
    }

    private void Remove(Visual page)
    {
        if (ReferenceEquals(page, _bars))
        {
            page.StopAnimation("Opacity");
            page.Opacity = 0f; // столбики переиспользуются: прячем, не удаляя
            return;
        }

        if (page.Parent is not null)
        {
            _content!.Children.Remove(page);
        }

        page.Dispose();
    }

    // ---------- движение ----------

    /// <summary>
    /// Выезд из-за нижнего края экрана (пружина) или разворот уже идущего скрытия от текущего состояния.
    /// При «Уменьшении движения» — растворение.
    /// </summary>
    public void Reveal(int widthPx, bool fromHidden)
    {
        var c = C;
        var p = _p!;
        _hideGeneration++; // скрытие, если шло, больше не завершит плашку
        if (_reducedMotion)
        {
            p.InsertScalar("Progress", 1f);
            p.InsertScalar("Width", widthPx);
            if (fromHidden)
            {
                _capsule!.Opacity = 0f;
            }

            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = OverlayMotion.ReducedMotionFade;
            _capsule!.StartAnimation("Opacity", fade);
            return;
        }

        if (fromHidden)
        {
            p.InsertScalar("Width", widthPx);
        }
        else
        {
            p.StartAnimation("Width", Spring(widthPx));
        }

        // Без InitialValue пружина стартует с текущего значения: при развороте скрытия нет скачка.
        p.StartAnimation("Progress", Spring(1f));
    }

    /// <summary>Смена ширины между состояниями той же пружиной; центр капсулы на месте.</summary>
    public void Resize(int widthPx)
    {
        if (_reducedMotion)
        {
            _p!.InsertScalar("Width", widthPx);
            return;
        }

        _p!.StartAnimation("Width", Spring(widthPx));
    }

    private SpringScalarNaturalMotionAnimation Spring(float finalValue)
    {
        var spring = C.CreateSpringScalarAnimation();
        spring.DampingRatio = OverlayMotion.SpringDampingRatio;
        spring.Period = TimeSpan.FromSeconds(OverlayMotion.SpringPeriodSeconds);
        spring.FinalValue = finalValue;
        return spring;
    }

    /// <summary>
    /// Скрытие: содержимое гаснет, капсула сжимается в круг и уезжает вниз (ease-in). По окончании вызывается
    /// <paramref name="done"/> — если за это время не было <see cref="Reveal"/>. Вызывается из очереди компоновщика:
    /// освобождать сцену прямо в нём нельзя (ShutdownQueueAsync зависнет), вызывающий откладывает освобождение.
    /// </summary>
    public void Conceal(Action done)
    {
        var c = C;
        var generation = ++_hideGeneration;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        if (_reducedMotion)
        {
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 0f);
            fade.Duration = OverlayMotion.ReducedMotionFade;
            _capsule!.StartAnimation("Opacity", fade);
        }
        else
        {
            var (x1, y1, x2, y2) = OverlayMotion.HideEasing;
            var away = c.CreateScalarKeyFrameAnimation();
            away.InsertKeyFrame(1f, 0f, c.CreateCubicBezierEasingFunction(new Vector2(x1, y1), new Vector2(x2, y2)));
            away.Duration = OverlayMotion.HideDuration;
            _p!.StartAnimation("Progress", away);
        }

        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation == _hideGeneration && _compositor is not null)
            {
                done();
            }
        };
    }

    /// <summary>Освобождает всё: дерево, цель, компоновщик, фабрику D2D, затем закрывает очередь потока.</summary>
    public void Dispose()
    {
        _hideGeneration++;
        try
        {
            if (_target is not null)
            {
                try
                {
                    _target.Root = null;
                }
                catch (Exception e)
                {
                    PlatformLog.Error("Плашка: отсоединение дерева", e); // DWM перезапускался: объекты уже недействительны
                }
            }

            _root?.Dispose();
            _target?.Dispose();
            _compositor?.Dispose();
        }
        finally
        {
            // Даже если закрытие объектов бросило, фабрика и очередь потока не должны пережить сцену.
            _root = _capsule = _material = _content = null;
            _bars = null;
            _barGeometries = [];
            _page = null;
            _p = null;
            _target = null;
            _compositor = null;
            Native.Release(_d2d);
            _d2d = 0;

            // Очередь закрывается асинхронно на цикле сообщений потока (за миллисекунды); результат не ждём.
            if (_queue is { } queue)
            {
                _queue = null;
                _ = queue.ShutdownQueueAsync();
            }
        }
    }

    private static Color WithAlpha(Color color, float alpha) => Color.FromArgb((byte)MathF.Round(alpha * 255f), color.R, color.G, color.B);
}
