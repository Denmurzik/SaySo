using System.Runtime.InteropServices;

namespace SaySo.Mac.Interop;

/// <summary>Частые обращения к AppKit/Foundation. Возвращаемые объекты — автоосвобождаемые, если не сказано иное.</summary>
internal static unsafe class NS
{
    public const long ActivationRegular = 0;
    public const long ActivationAccessory = 1;

    private static readonly nint AppKitHandle = NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");

    public static nint App => ObjC.Send(ObjC.Class("NSApplication"), ObjC.Sel("sharedApplication"));

    public static nint Workspace => ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace"));

    public static nint Sel(string name) => ObjC.Sel(name);

    public static nint Str(string text) => ObjC.Str(text);

    /// <summary>Строковая константа AppKit (экспортированная NSString *).</summary>
    public static nint AppKitString(string symbol) => *(nint*)NativeLibrary.GetExport(AppKitHandle, symbol);

    public static void SetActivationPolicy(long policy) => ObjC.Send(App, Sel("setActivationPolicy:"), (nint)policy);

    /// <summary>Вывести приложение на передний план (окно настроек).</summary>
    public static void ActivateApp() => ObjC.SendVoid(App, Sel("activateIgnoringOtherApps:"), true);

    public static nint Array(params ReadOnlySpan<nint> items)
    {
        fixed (nint* p = items)
        {
            return ObjC.Send(ObjC.Class("NSArray"), Sel("arrayWithObjects:count:"), (nint)p, items.Length);
        }
    }

    public static long Count(nint array) => array == 0 ? 0 : ObjC.SendLong(array, Sel("count"));

    public static nint At(nint array, long index) => ObjC.Send(array, Sel("objectAtIndex:"), (nint)index);

    public static nint Url(string url) => ObjC.Send(ObjC.Class("NSURL"), Sel("URLWithString:"), Str(url));

    public static nint FileUrl(string path) => ObjC.Send(ObjC.Class("NSURL"), Sel("fileURLWithPath:"), Str(path));

    /// <summary>Открыть адрес в браузере по умолчанию или раздел Системных настроек (x-apple.systempreferences:…).</summary>
    public static bool Open(string url) => ObjC.SendBool(Workspace, Sel("openURL:"), Url(url));

    public static void OpenFolder(string path) => ObjC.SendBool(Workspace, Sel("openURL:"), FileUrl(path));

    // ---------- цвета и шрифты ----------

    public static nint Color(string name) => ObjC.Send(ObjC.Class("NSColor"), Sel(name));

    public static nint Rgba(double r, double g, double b, double a) =>
        ObjC.Send(ObjC.Class("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"), r, g, b, a);

    /// <summary>CGColorRef цвета (живёт, пока жив NSColor; для слоёв Core Animation).</summary>
    public static nint CgColor(nint nsColor) => ObjC.Send(nsColor, Sel("CGColor"));

    public static nint Font(double size) => ObjC.Send(ObjC.Class("NSFont"), Sel("systemFontOfSize:"), size);

    public static nint BoldFont(double size) => ObjC.Send(ObjC.Class("NSFont"), Sel("boldSystemFontOfSize:"), size);

    /// <summary>Системный шрифт заданной насыщенности (NSFontWeight: 0 обычный, 0.23 полужирный, 0.4 жирный).</summary>
    public static nint Font(double size, double weight) =>
        ObjC.Send(ObjC.Class("NSFont"), Sel("systemFontOfSize:weight:"), size, weight);

    // ---------- изображения ----------

    /// <summary>Значок SF Symbols (macOS 11+); null-указатель, если такого символа нет.</summary>
    public static nint Symbol(string name, string? description = null) =>
        ObjC.Send(ObjC.Class("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"), Str(name),
            description is null ? 0 : Str(description));

    // ---------- приложения ----------

    /// <summary>PID приложения на переднем плане (того, кто получит Cmd+V); 0 — неизвестно.</summary>
    public static int FrontmostPid()
    {
        var app = ObjC.Send(Workspace, Sel("frontmostApplication"));
        return app == 0 ? 0 : (int)ObjC.SendLong(app, Sel("processIdentifier"));
    }

    /// <summary>Приложение с этим PID ещё работает.</summary>
    public static bool IsRunning(int pid) =>
        pid > 0 && ObjC.Send(ObjC.Class("NSRunningApplication"), Sel("runningApplicationWithProcessIdentifier:"), (nint)pid) != 0;

    /// <summary>Вывести приложение с этим PID на передний план (NSApplicationActivateIgnoringOtherApps).</summary>
    public static bool Activate(int pid)
    {
        var app = ObjC.Send(ObjC.Class("NSRunningApplication"), Sel("runningApplicationWithProcessIdentifier:"), (nint)pid);
        return app != 0 && ObjC.SendBool(app, Sel("activateWithOptions:"), (nint)2);
    }

    /// <summary>«Уменьшить движение» в настройках Универсального доступа.</summary>
    public static bool ReduceMotion => ObjC.SendBool(Workspace, Sel("accessibilityDisplayShouldReduceMotion"));

    /// <summary>«Уменьшить прозрачность» в настройках Универсального доступа.</summary>
    public static bool ReduceTransparency => ObjC.SendBool(Workspace, Sel("accessibilityDisplayShouldReduceTransparency"));

    // ---------- уведомления ----------

    public static nint NotificationCenter => ObjC.Send(ObjC.Class("NSNotificationCenter"), Sel("defaultCenter"));

    public static nint WorkspaceNotificationCenter => ObjC.Send(Workspace, Sel("notificationCenter"));

    public static nint DistributedNotificationCenter => ObjC.Send(ObjC.Class("NSDistributedNotificationCenter"), Sel("defaultCenter"));

    /// <summary>Подписка <paramref name="observer"/> (его метод notify:) на уведомление.</summary>
    public static void Observe(nint center, nint observer, string name, nint obj = 0) =>
        ObjC.SendVoid(center, Sel("addObserver:selector:name:object:"), observer, Bridge.NotifySelector, Str(name), obj);

    public static void Unobserve(nint center, nint observer) => ObjC.SendVoid(center, Sel("removeObserver:"), observer);

    public static string? NotificationName(nint notification) => ObjC.ToStr(ObjC.Send(notification, Sel("name")));
}
