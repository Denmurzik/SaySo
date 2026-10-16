using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;

namespace SaySo.Mac.Interop;

/// <summary>
/// Объект Objective-C, который AppKit вызывает как target/action, делегат приложения, окна, меню, текстового поля и
/// панели инструментов или как наблюдатель уведомлений. Один класс «SaySoBridge» на все роли: каждый экземпляр связан
/// со своим управляемым <see cref="Bridge"/>, а у того заданы только нужные обработчики (остальные методы возвращают то
/// же, что AppKit предполагает без делегата). AppKit не удерживает target и делегатов: экземпляр живёт, пока жив
/// <see cref="Bridge"/> (до <see cref="Dispose"/>). Все вызовы — на главном потоке.
/// </summary>
internal sealed unsafe class Bridge : IDisposable
{
    private const nuint TerminateNow = 1;

    private static readonly Dictionary<nint, Bridge> Live = [];
    private static readonly string BoolType = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "B" : "c";
    private static nint _class;

    public Bridge()
    {
        Handle = ObjC.Send(ObjC.Send(Class, ObjC.Sel("alloc")), ObjC.Sel("init"));
        Live[Handle] = this;
    }

    public Bridge(Action<nint> invoked)
        : this()
    {
        Invoked = invoked;
    }

    /// <summary>Экземпляр SaySoBridge (+1, освобождается в <see cref="Dispose"/>).</summary>
    public nint Handle { get; private set; }

    /// <summary>Селектор действия для target/action: <c>invoke:</c>.</summary>
    public static nint Action => ObjC.Sel("invoke:");

    /// <summary>Селектор наблюдателя уведомлений: <c>notify:</c>.</summary>
    public static nint NotifySelector => ObjC.Sel("notify:");

    public Action<nint>? Invoked { get; set; }

    public Action<nint>? Notified { get; set; }

    public Func<bool>? ShouldHandleReopen { get; set; }

    /// <summary>applicationShouldTerminate: — NSTerminateCancel (0), NSTerminateNow (1) или NSTerminateLater (2).</summary>
    public Func<nuint>? ShouldTerminate { get; set; }

    public bool TerminateAfterLastWindowClosed { get; set; }

    public Action? WindowWillClose { get; set; }

    public Action? WindowDidResignKey { get; set; }

    public Action<nint>? MenuNeedsUpdate { get; set; }

    public Action<nint>? TextDidChange { get; set; }

    public Action<nint>? TextDidEndEditing { get; set; }

    public Func<string, nint>? ToolbarItem { get; set; }

    public Func<nint>? ToolbarIdentifiers { get; set; }

    private static nint Class => _class != 0 ? _class : _class = ObjC.DefineClass("SaySoBridge", "NSObject",
        ("invoke:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnInvoke, "v@:@"),
        ("notify:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnNotify, "v@:@"),
        ("applicationShouldHandleReopen:hasVisibleWindows:", (nint)(delegate* unmanaged<nint, nint, nint, byte, byte>)&OnReopen, BoolType + "@:@" + BoolType),
        ("applicationShouldTerminate:", (nint)(delegate* unmanaged<nint, nint, nint, nuint>)&OnShouldTerminate, "Q@:@"),
        ("applicationShouldTerminateAfterLastWindowClosed:", (nint)(delegate* unmanaged<nint, nint, nint, byte>)&OnTerminateAfterLastWindow, BoolType + "@:@"),
        ("windowWillClose:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnWindowWillClose, "v@:@"),
        ("windowDidResignKey:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnWindowDidResignKey, "v@:@"),
        ("menuNeedsUpdate:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnMenuNeedsUpdate, "v@:@"),
        ("controlTextDidChange:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnTextDidChange, "v@:@"),
        ("controlTextDidEndEditing:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnTextDidEndEditing, "v@:@"),
        ("textDidChange:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnTextDidChange, "v@:@"),
        ("textDidEndEditing:", (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnTextDidEndEditing, "v@:@"),
        ("toolbar:itemForItemIdentifier:willBeInsertedIntoToolbar:", (nint)(delegate* unmanaged<nint, nint, nint, nint, byte, nint>)&OnToolbarItem, "@@:@@" + BoolType),
        ("toolbarAllowedItemIdentifiers:", (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnToolbarIdentifiers, "@@:@"),
        ("toolbarDefaultItemIdentifiers:", (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnToolbarIdentifiers, "@@:@"),
        ("toolbarSelectableItemIdentifiers:", (nint)(delegate* unmanaged<nint, nint, nint, nint>)&OnToolbarIdentifiers, "@@:@"));

    public void Dispose()
    {
        if (Handle == 0)
        {
            return;
        }

        Live.Remove(Handle);
        ObjC.Release(Handle);
        Handle = 0;
    }

    private static Bridge? Find(nint self) => Live.GetValueOrDefault(self);

    private static void Guard(string what, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            // Исключение не должно уйти в AppKit: процесс упал бы без записи в журнал.
            Log.Error(what, e);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnInvoke(nint self, nint sel, nint sender)
    {
        if (Find(self)?.Invoked is { } handler)
        {
            Guard("Действие AppKit", () => handler(sender));
        }
    }

    [UnmanagedCallersOnly]
    private static void OnNotify(nint self, nint sel, nint notification)
    {
        if (Find(self)?.Notified is { } handler)
        {
            Guard("Уведомление", () => handler(notification));
        }
    }

    [UnmanagedCallersOnly]
    private static byte OnReopen(nint self, nint sel, nint app, byte hasVisibleWindows)
    {
        var result = true;
        if (Find(self)?.ShouldHandleReopen is { } handler)
        {
            Guard("Повторное открытие", () => result = handler());
        }

        return result ? (byte)1 : (byte)0;
    }

    [UnmanagedCallersOnly]
    private static nuint OnShouldTerminate(nint self, nint sel, nint app)
    {
        var result = TerminateNow;
        if (Find(self)?.ShouldTerminate is { } handler)
        {
            Guard("Завершение приложения", () => result = handler());
        }

        return result;
    }

    [UnmanagedCallersOnly]
    private static byte OnTerminateAfterLastWindow(nint self, nint sel, nint app) =>
        Find(self)?.TerminateAfterLastWindowClosed == true ? (byte)1 : (byte)0;

    [UnmanagedCallersOnly]
    private static void OnWindowWillClose(nint self, nint sel, nint notification)
    {
        if (Find(self)?.WindowWillClose is { } handler)
        {
            Guard("Закрытие окна", handler);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnWindowDidResignKey(nint self, nint sel, nint notification)
    {
        if (Find(self)?.WindowDidResignKey is { } handler)
        {
            Guard("Окно потеряло фокус", handler);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnMenuNeedsUpdate(nint self, nint sel, nint menu)
    {
        if (Find(self)?.MenuNeedsUpdate is { } handler)
        {
            Guard("Меню", () => handler(menu));
        }
    }

    [UnmanagedCallersOnly]
    private static void OnTextDidChange(nint self, nint sel, nint notification)
    {
        if (Find(self)?.TextDidChange is { } handler)
        {
            Guard("Ввод текста", () => handler(notification));
        }
    }

    [UnmanagedCallersOnly]
    private static void OnTextDidEndEditing(nint self, nint sel, nint notification)
    {
        if (Find(self)?.TextDidEndEditing is { } handler)
        {
            Guard("Конец ввода текста", () => handler(notification));
        }
    }

    [UnmanagedCallersOnly]
    private static nint OnToolbarItem(nint self, nint sel, nint toolbar, nint identifier, byte willBeInserted)
    {
        nint item = 0;
        if (Find(self)?.ToolbarItem is { } handler)
        {
            Guard("Панель инструментов", () => item = handler(ObjC.ToStr(identifier) ?? ""));
        }

        return item;
    }

    [UnmanagedCallersOnly]
    private static nint OnToolbarIdentifiers(nint self, nint sel, nint toolbar)
    {
        nint ids = 0;
        if (Find(self)?.ToolbarIdentifiers is { } handler)
        {
            Guard("Панель инструментов", () => ids = handler());
        }

        return ids;
    }
}
