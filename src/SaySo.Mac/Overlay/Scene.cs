using SaySo.Core.Overlay;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Overlay;

/// <summary>
/// Окно и слои одного показа плашки. Координаты — точки macOS (DIP), начало сверху слева: раскладка та же, что в Windows
/// (<see cref="OverlayMotion.Layout"/> c масштабом 1), окно стоит над нижним краем экрана, капсула ездит внутри него.
/// </summary>
internal sealed class Scene : IDisposable
{
    private const long StyleBorderless = 0;
    private const long StyleNonactivatingPanel = 1 << 7;
    private const long BackingBuffered = 2;
    private const long ScreenSaverLevel = 1000;      // выше «всегда поверх», Dock и полноэкранных приложений
    private const long JoinAllSpaces = 1 << 0;       // NSWindowCollectionBehaviorCanJoinAllSpaces
    private const long Stationary = 1 << 4;          // не уезжает при смене рабочего стола
    private const long IgnoresCycle = 1 << 6;        // нет в Cmd+`
    private const long FullScreenAuxiliary = 1 << 8; // показывается и поверх полноэкранного приложения
    private const long MaterialHudWindow = 13;
    private const long BlendingBehindWindow = 0;
    private const long EffectStateActive = 1;

    private readonly nint _panel;
    private readonly nint _effect;
    private readonly nint _capsule;
    private readonly nint _content;
    private readonly nint _bars;
    private readonly nint[] _barLayers = new nint[OverlayMotion.BarCount];
    private readonly nint _dots;
    private readonly nint[] _dotLayers = new nint[3];
    private readonly nint _message;
    private readonly nint _toneDot;
    private readonly nint _text;
    private readonly double _scale;

    private Scene(nint panel, nint effect, nint capsule, nint content, OverlayLayout layout, double scale, bool reducedMotion, bool solid)
    {
        _panel = panel;
        _effect = effect;
        _capsule = capsule;
        _content = content;
        Layout = layout;
        _scale = scale;
        ReducedMotion = reducedMotion;

        _bars = Group(content);
        for (var i = 0; i < _barLayers.Length; i++)
        {
            _barLayers[i] = Shape(_bars, NS.Rgba(1, 1, 1, 0.95), OverlayMotion.BarWidth / 2);
        }

        _dots = Group(content);
        var centers = OverlayMotion.DotCenters(layout);
        for (var i = 0; i < _dotLayers.Length; i++)
        {
            var r = OverlayMotion.DotRadius;
            _dotLayers[i] = Shape(_dots, NS.Rgba(1, 1, 1, 1), r);
            Layers.Frame(_dotLayers[i], centers[i].X - r, centers[i].Y - r, 2 * r, 2 * r);
        }

        _message = Group(content);
        _toneDot = Shape(_message, NS.Rgba(1, 1, 1, 1), OverlayMotion.ToneDotSize / 2);
        _text = Layers.NewText();
        ObjC.SendVoid(_text, ObjC.Sel("setFont:"), Font);
        ObjC.SendVoid(_text, ObjC.Sel("setFontSize:"), (double)OverlayMotion.FontSize);
        ObjC.SendVoid(_text, ObjC.Sel("setForegroundColor:"), NS.CgColor(NS.Rgba(1, 1, 1, 0.95)));
        ObjC.SendVoid(_text, ObjC.Sel("setTruncationMode:"), Layers.TruncationEnd);
        ObjC.SendVoid(_text, ObjC.Sel("setAlignmentMode:"), Layers.AlignmentLeft);
        Layers.Scale(_text, scale);
        Layers.Add(_message, _text);
        ObjC.Release(_text);

        if (solid)
        {
            ObjC.SendVoid(_effect, ObjC.Sel("setHidden:"), true);
        }
    }

    public OverlayLayout Layout { get; }

    public bool ReducedMotion { get; }

    private static nint Font => NS.Font(OverlayMotion.FontSize);

    public static Scene Create()
    {
        var screen = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Sel("mainScreen")); // экран с окном, у которого фокус клавиатуры
        if (screen == 0)
        {
            var screens = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Sel("screens"));
            screen = NS.Count(screens) > 0 ? NS.At(screens, 0) : 0;
        }

        if (screen == 0)
        {
            throw new InvalidOperationException("Нет ни одного экрана.");
        }

        var frame = ObjC.SendRect(screen, ObjC.Sel("frame"));
        var visible = ObjC.SendRect(screen, ObjC.Sel("visibleFrame"));
        var scale = Math.Max(1, ObjC.SendDouble(screen, ObjC.Sel("backingScaleFactor")));

        // Раскладка Windows считается сверху вниз от верха монитора; у Cocoa начало снизу — переводим.
        var layout = OverlayMotion.Layout(1f, (int)frame.Height, (int)(visible.X - frame.X), (int)(visible.MaxX - frame.X),
            (int)(frame.MaxY - visible.Y));
        var rect = new CGRect(frame.X + layout.WindowLeft, frame.MaxY - layout.WindowTop - layout.WindowHeight,
            layout.WindowWidth, layout.WindowHeight);

        var panel = ObjC.Send(ObjC.Alloc("NSPanel"), ObjC.Sel("initWithContentRect:styleMask:backing:defer:"), rect,
            (nint)(StyleBorderless | StyleNonactivatingPanel), (nint)BackingBuffered, false);
        ObjC.SendVoid(panel, ObjC.Sel("setReleasedWhenClosed:"), false);
        ObjC.SendVoid(panel, ObjC.Sel("setOpaque:"), false);
        ObjC.SendVoid(panel, ObjC.Sel("setBackgroundColor:"), NS.Color("clearColor"));
        ObjC.SendVoid(panel, ObjC.Sel("setHasShadow:"), false);
        ObjC.SendVoid(panel, ObjC.Sel("setIgnoresMouseEvents:"), true);
        ObjC.SendVoid(panel, ObjC.Sel("setHidesOnDeactivate:"), false);
        ObjC.SendVoid(panel, ObjC.Sel("setCanHide:"), false);
        ObjC.Send(panel, ObjC.Sel("setLevel:"), (nint)ScreenSaverLevel);
        ObjC.Send(panel, ObjC.Sel("setCollectionBehavior:"), (nint)(JoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary));
        ObjC.SendVoid(panel, ObjC.Sel("setAppearance:"),
            ObjC.Send(ObjC.Class("NSAppearance"), ObjC.Sel("appearanceNamed:"), NS.AppKitString("NSAppearanceNameDarkAqua")));

        var bounds = new CGRect(0, 0, layout.WindowWidth, layout.WindowHeight);
        var root = ObjC.Send(ObjC.Send(Layers.FlippedViewClass, ObjC.Sel("alloc")), ObjC.Sel("initWithFrame:"), bounds);
        ObjC.SendVoid(root, ObjC.Sel("setWantsLayer:"), true);
        ObjC.SendVoid(panel, ObjC.Sel("setContentView:"), root);
        ObjC.Release(root);

        var effect = ObjC.Send(ObjC.Alloc("NSVisualEffectView"), ObjC.Sel("initWithFrame:"), new CGRect(0, 0, layout.CapsuleHeight, layout.CapsuleHeight));
        ObjC.Send(effect, ObjC.Sel("setMaterial:"), (nint)MaterialHudWindow);
        ObjC.Send(effect, ObjC.Sel("setBlendingMode:"), (nint)BlendingBehindWindow);
        ObjC.Send(effect, ObjC.Sel("setState:"), (nint)EffectStateActive);
        ObjC.SendVoid(effect, ObjC.Sel("setWantsLayer:"), true);
        var effectLayer = ObjC.Send(effect, ObjC.Sel("layer"));
        Layers.Corner(effectLayer, layout.CapsuleHeight / 2.0);
        Layers.Clips(effectLayer, true);
        ObjC.SendVoid(root, ObjC.Sel("addSubview:"), effect);
        ObjC.Release(effect);

        // Слой-хост поверх размытия: тонировка, рамка и содержимое — свои слои, AppKit их не трогает. Его координаты — от
        // нижнего края (как у CALayer по умолчанию), поэтому Y капсулы переводится в Render; содержимое внутри капсулы
        // симметрично по вертикали и от направления оси не зависит.
        var host = ObjC.Send(ObjC.Alloc("NSView"), ObjC.Sel("initWithFrame:"), bounds);
        var hostLayer = Layers.New();
        ObjC.SendVoid(host, ObjC.Sel("setLayer:"), hostLayer);
        ObjC.SendVoid(host, ObjC.Sel("setWantsLayer:"), true);
        ObjC.Release(hostLayer);
        ObjC.SendVoid(root, ObjC.Sel("addSubview:"), host);
        ObjC.Release(host);

        var solid = NS.ReduceTransparency;
        var capsule = Layers.New();
        Layers.Corner(capsule, layout.CapsuleHeight / 2.0);
        Layers.Clips(capsule, true);
        Layers.Background(capsule, NS.Rgba(0x16 / 255.0, 0x16 / 255.0, 0x18 / 255.0, solid ? 0.88 : 0.35));
        Layers.Border(capsule, 1 / scale, NS.Rgba(1, 1, 1, 0.14)); // рамка в один физический пиксель, как у окон
        Layers.Add(hostLayer, capsule);
        ObjC.Release(capsule);

        var content = Layers.New();
        Layers.Add(capsule, content);
        ObjC.Release(content);

        return new Scene(panel, effect, capsule, content, layout, scale, NS.ReduceMotion, solid);
    }

    /// <summary>Показать окно (если скрыто) и поставить его поверх всех без активации.</summary>
    public void Raise() => ObjC.SendVoid(_panel, ObjC.Sel("orderFrontRegardless"));

    public void SetLevels(ReadOnlySpan<float> levels)
    {
        var bars = OverlayMotion.Bars(levels, Layout);
        Layers.Batch(() =>
        {
            for (var i = 0; i < bars.Length; i++)
            {
                Layers.Frame(_barLayers[i], bars[i].X, bars[i].Y, bars[i].Width, bars[i].Height);
            }
        });
    }

    /// <returns>Ширина капсулы сообщения.</returns>
    public int SetMessage(string text, OverlayTone tone)
    {
        var (textWidth, lineHeight) = Measure(text);
        var shownWidth = Math.Min(textWidth, OverlayMotion.MaxTextWidth);
        var width = OverlayMotion.MessageWidthPx((float)shownWidth, Layout);
        var parts = OverlayMotion.MessageParts(width, Layout);
        var color = tone switch
        {
            OverlayTone.Error => NS.Rgba(0xE8 / 255.0, 0x4B / 255.0, 0x4B / 255.0, 1),
            OverlayTone.Info => NS.Rgba(0xF2 / 255.0, 0xB3 / 255.0, 0x3D / 255.0, 1),
            _ => NS.Rgba(1, 1, 1, 1),
        };
        Layers.Batch(() =>
        {
            Layers.Background(_toneDot, color);
            Layers.Frame(_toneDot, parts.DotX - parts.DotRadius, parts.DotY - parts.DotRadius, 2 * parts.DotRadius, 2 * parts.DotRadius);
            ObjC.SendVoid(_text, ObjC.Sel("setString:"), ObjC.Str(text));
            Layers.Frame(_text, parts.TextLeft, (Layout.CapsuleHeight - lineHeight) / 2, shownWidth + 1, lineHeight);
        });
        return width;
    }

    /// <summary>Кадр: положение и ширина капсулы, прозрачность окна и групп содержимого, яркость точек.</summary>
    public void Render(double progress, double width, double windowAlpha, double bars, double dots, double message, float dotPhase)
    {
        var p = (float)progress;
        var h = Layout.CapsuleHeight;
        var max = Layout.CapsuleMaxWidth;
        var w = OverlayMotion.CapsuleWidth(p, h, (float)width);
        var x = Layout.CapsuleLeft + (max - w) / 2.0;
        var y = OverlayMotion.CapsuleY(p, Layout.StartY, Layout.EndY);
        var content = OverlayMotion.ContentOpacity(p);
        Layers.Batch(() =>
        {
            ObjC.SendVoid(_panel, ObjC.Sel("setAlphaValue:"), windowAlpha);
            ObjC.SendVoid(_effect, ObjC.Sel("setFrame:"), new CGRect(x, y, w, h));
            Layers.Frame(_capsule, x, Layout.WindowHeight - y - h, w, h); // у слоя-хоста начало снизу
            Layers.Frame(_content, -(max - w) / 2.0, 0, max, h); // содержимое в координатах коробки капсулы
            Layers.Opacity(_content, content);
            Layers.Opacity(_bars, bars);
            Layers.Opacity(_dots, dots);
            Layers.Opacity(_message, message);
            for (var i = 0; i < _dotLayers.Length; i++)
            {
                Layers.Opacity(_dotLayers[i], OverlayMotion.DotOpacity(i, dotPhase));
            }
        });
    }

    public void Dispose()
    {
        ObjC.SendVoid(_panel, ObjC.Sel("orderOut:"), (nint)0);
        ObjC.SendVoid(_panel, ObjC.Sel("close"));
        ObjC.Release(_panel);
    }

    private static (double Width, double Height) Measure(string text)
    {
        var attributes = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Sel("dictionaryWithObject:forKey:"), Font,
            NS.AppKitString("NSFontAttributeName"));
        var attributed = ObjC.Send(ObjC.Alloc("NSAttributedString"), ObjC.Sel("initWithString:attributes:"), ObjC.Str(text), attributes);
        var size = ObjC.SendSize(attributed, ObjC.Sel("size"));
        ObjC.Release(attributed);
        return (Math.Ceiling(size.Width), Math.Ceiling(size.Height));
    }

    private nint Group(nint parent)
    {
        var group = Layers.New();
        Layers.Frame(group, 0, 0, Layout.CapsuleMaxWidth, Layout.CapsuleHeight);
        Layers.Add(parent, group);
        ObjC.Release(group);
        return group;
    }

    private static nint Shape(nint parent, nint color, double radius)
    {
        var layer = Layers.New();
        Layers.Background(layer, color);
        Layers.Corner(layer, radius);
        Layers.Add(parent, layer);
        ObjC.Release(layer);
        return layer;
    }
}
