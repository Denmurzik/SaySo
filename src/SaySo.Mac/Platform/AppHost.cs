using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>Запуск NSApplication в этом процессе: политика активации, делегат, цикл событий и выход из него.</summary>
internal static class AppHost
{
    /// <summary>
    /// Подготовить приложение: <paramref name="policy"/> — <see cref="NS.ActivationAccessory"/> (демон: без значка в Dock,
    /// как LSUIElement) или <see cref="NS.ActivationRegular"/> (окно настроек: значок в Dock, пока окно открыто).
    /// </summary>
    public static void Initialize(long policy, Bridge? appDelegate = null)
    {
        SynchronizationContext.SetSynchronizationContext(new MainThreadSynchronizationContext());
        var app = NS.App;
        NS.SetActivationPolicy(policy);
        if (appDelegate is not null)
        {
            ObjC.SendVoid(app, ObjC.Sel("setDelegate:"), appDelegate.Handle);
        }

        ObjC.SendVoid(app, ObjC.Sel("finishLaunching"));
    }

    /// <summary>Цикл событий AppKit до <see cref="Stop"/>.</summary>
    public static void Run() => ObjC.SendVoid(NS.App, ObjC.Sel("run"));

    /// <summary>
    /// Выйти из <see cref="Run"/>. stop: срабатывает после обработки очередного события, поэтому следом ставится пустое
    /// событие — иначе цикл ждал бы следующего ввода пользователя.
    /// </summary>
    public static void Stop()
    {
        var app = NS.App;
        ObjC.SendVoid(app, ObjC.Sel("stop:"), (nint)0);
        nint ev;
        unsafe
        {
            // (NSEventType, NSPoint, NSEventModifierFlags, NSTimeInterval, NSInteger, NSGraphicsContext*, short, NSInteger, NSInteger)
            ev = ((delegate* unmanaged<nint, nint, nint, CGPoint, nint, double, nint, nint, short, nint, nint, nint>)ObjC.MsgSend)(
                ObjC.Class("NSEvent"),
                ObjC.Sel("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:"),
                EventApplicationDefined, new CGPoint(0, 0), 0, 0, 0, 0, 0, 0, 0);
        }

        ObjC.SendVoid(app, ObjC.Sel("postEvent:atStart:"), ev, true);
    }

    private const nint EventApplicationDefined = 15;
}
