using System.Runtime.InteropServices;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Overlay;

/// <summary>Core Animation для плашки: слои, текстовый слой и транзакции без неявных анимаций.</summary>
internal static unsafe class Layers
{
    private static readonly nint QuartzCore = NativeLibrary.Load("/System/Library/Frameworks/QuartzCore.framework/QuartzCore");
    private static nint _flippedView;

    public static nint TruncationEnd => *(nint*)NativeLibrary.GetExport(QuartzCore, "kCATruncationEnd");

    public static nint AlignmentLeft => *(nint*)NativeLibrary.GetExport(QuartzCore, "kCAAlignmentLeft");

    /// <summary>Новый CALayer (+1).</summary>
    public static nint New() => ObjC.New("CALayer");

    public static nint NewText() => ObjC.New("CATextLayer");

    public static void Add(nint parent, nint child) => ObjC.SendVoid(parent, ObjC.Sel("addSublayer:"), child);

    public static void Frame(nint layer, double x, double y, double w, double h) =>
        ObjC.SendVoid(layer, ObjC.Sel("setFrame:"), new CGRect(x, y, w, h));

    public static void Background(nint layer, nint nsColor) =>
        ObjC.SendVoid(layer, ObjC.Sel("setBackgroundColor:"), nsColor == 0 ? 0 : NS.CgColor(nsColor));

    public static void Corner(nint layer, double radius) => ObjC.SendVoid(layer, ObjC.Sel("setCornerRadius:"), radius);

    public static void Opacity(nint layer, double opacity) =>
        ObjC.SendVoid(layer, ObjC.Sel("setOpacity:"), (float)Math.Clamp(opacity, 0, 1));

    public static void Hidden(nint layer, bool hidden) => ObjC.SendVoid(layer, ObjC.Sel("setHidden:"), hidden);

    public static void Clips(nint layer, bool clips) => ObjC.SendVoid(layer, ObjC.Sel("setMasksToBounds:"), clips);

    public static void Border(nint layer, double width, nint nsColor)
    {
        ObjC.SendVoid(layer, ObjC.Sel("setBorderWidth:"), width);
        ObjC.SendVoid(layer, ObjC.Sel("setBorderColor:"), NS.CgColor(nsColor));
    }

    public static void Scale(nint layer, double scale) => ObjC.SendVoid(layer, ObjC.Sel("setContentsScale:"), scale);

    /// <summary>Набор изменений без неявных анимаций Core Animation (кадры считает сама плашка).</summary>
    public static void Batch(Action body)
    {
        var tx = ObjC.Class("CATransaction");
        ObjC.SendVoid(tx, ObjC.Sel("begin"));
        ObjC.SendVoid(tx, ObjC.Sel("setDisableActions:"), true);
        try
        {
            body();
        }
        finally
        {
            ObjC.SendVoid(tx, ObjC.Sel("commit"));
        }
    }

    /// <summary>Класс NSView с перевёрнутыми координатами (начало сверху слева, как у плашки Windows).</summary>
    public static nint FlippedViewClass => _flippedView != 0 ? _flippedView : _flippedView = ObjC.DefineClass("SaySoFlippedView", "NSView",
        ("isFlipped", (nint)(delegate* unmanaged<nint, nint, byte>)&IsFlipped,
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B@:" : "c@:"));

    [UnmanagedCallersOnly]
    private static byte IsFlipped(nint self, nint sel) => 1;
}
