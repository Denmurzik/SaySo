using System.Runtime.InteropServices;

namespace SaySo.Mac.Interop;

/// <summary>CoreGraphics (события клавиатуры, перехват CGEventTap), CoreFoundation (циклы, строки) и ApplicationServices (доступ).</summary>
internal static unsafe partial class Quartz
{
    public const string CoreGraphicsLib = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    public const string CoreFoundationLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    public const string ApplicationServicesLib = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    public const string CarbonLib = "/System/Library/Frameworks/Carbon.framework/Carbon";

    // CGEventType
    public const int KeyDown = 10;
    public const int KeyUp = 11;
    public const int FlagsChanged = 12;
    public const int TapDisabledByTimeout = unchecked((int)0xFFFFFFFE);
    public const int TapDisabledByUserInput = unchecked((int)0xFFFFFFFF);

    // CGEventField
    public const int KeyboardEventAutorepeat = 8;
    public const int KeyboardEventKeycode = 9;
    public const int EventSourceUserData = 42;
    public const int EventSourceStateId = 45;

    // CGEventSourceStateID
    public const int StateCombinedSession = 0;
    public const int StateHidSystem = 1;
    public const int StatePrivate = -1;

    // CGEventTapLocation / Placement / Options
    public const int SessionEventTap = 1;
    public const int HeadInsertEventTap = 0;
    public const int TapOptionDefault = 0;

    public const ulong FlagCommand = 0x0010_0000; // kCGEventFlagMaskCommand

    private static readonly nint CoreFoundation = NativeLibrary.Load(CoreFoundationLib);

    public static readonly nint RunLoopCommonModes = *(nint*)NativeLibrary.GetExport(CoreFoundation, "kCFRunLoopCommonModes");

    public static readonly nint RunLoopDefaultMode = *(nint*)NativeLibrary.GetExport(CoreFoundation, "kCFRunLoopDefaultMode");

    public static ulong EventMask(params int[] types)
    {
        ulong mask = 0;
        foreach (var type in types)
        {
            mask |= 1UL << type;
        }

        return mask;
    }

    // ---------- CGEvent ----------

    [LibraryImport(CoreGraphicsLib)]
    public static partial nint CGEventTapCreate(int tap, int place, int options, ulong eventsOfInterest,
        delegate* unmanaged<nint, int, nint, nint, nint> callback, nint userInfo);

    [LibraryImport(CoreGraphicsLib)]
    public static partial void CGEventTapEnable(nint tap, byte enable);

    [LibraryImport(CoreGraphicsLib)]
    public static partial byte CGEventTapIsEnabled(nint tap);

    [LibraryImport(CoreGraphicsLib)]
    public static partial long CGEventGetIntegerValueField(nint ev, int field);

    [LibraryImport(CoreGraphicsLib)]
    public static partial void CGEventSetIntegerValueField(nint ev, int field, long value);

    [LibraryImport(CoreGraphicsLib)]
    public static partial ulong CGEventGetFlags(nint ev);

    [LibraryImport(CoreGraphicsLib)]
    public static partial void CGEventSetFlags(nint ev, ulong flags);

    [LibraryImport(CoreGraphicsLib)]
    public static partial nint CGEventSourceCreate(int stateId);

    [LibraryImport(CoreGraphicsLib)]
    public static partial nint CGEventCreateKeyboardEvent(nint source, ushort keyCode, byte keyDown);

    [LibraryImport(CoreGraphicsLib)]
    public static partial void CGEventPost(int tap, nint ev);

    [LibraryImport(CoreGraphicsLib)]
    public static partial byte CGEventSourceKeyState(int stateId, ushort keyCode);

    // ---------- CoreFoundation ----------

    [LibraryImport(CoreFoundationLib)]
    public static partial nint CFMachPortCreateRunLoopSource(nint allocator, nint port, nint order);

    [LibraryImport(CoreFoundationLib)]
    public static partial void CFMachPortInvalidate(nint port);

    [LibraryImport(CoreFoundationLib)]
    public static partial nint CFRunLoopGetCurrent();

    [LibraryImport(CoreFoundationLib)]
    public static partial void CFRunLoopAddSource(nint loop, nint source, nint mode);

    [LibraryImport(CoreFoundationLib)]
    public static partial void CFRunLoopRemoveSource(nint loop, nint source, nint mode);

    [LibraryImport(CoreFoundationLib)]
    public static partial int CFRunLoopRunInMode(nint mode, double seconds, byte returnAfterSourceHandled);

    [LibraryImport(CoreFoundationLib)]
    public static partial void CFRunLoopStop(nint loop);

    [LibraryImport(CoreFoundationLib)]
    public static partial void CFRunLoopWakeUp(nint loop);

    [LibraryImport(CoreFoundationLib)]
    public static partial void CFRelease(nint obj);

    [LibraryImport(CoreFoundationLib)]
    public static partial nint CFRetain(nint obj);

    [LibraryImport(CoreFoundationLib)]
    public static partial nint CFDataGetBytePtr(nint data);

    // ---------- доступ («Универсальный доступ») ----------

    [LibraryImport(ApplicationServicesLib)]
    public static partial byte AXIsProcessTrusted();

    [LibraryImport(ApplicationServicesLib)]
    public static partial byte AXIsProcessTrustedWithOptions(nint options);

    /// <summary>Включён «защищённый ввод» (поле пароля, Secure Keyboard Entry терминала): перехват клавиш его не видит.</summary>
    [LibraryImport(CarbonLib)]
    public static partial byte IsSecureEventInputEnabled();

    // ---------- раскладка (чтобы найти клавишу «V» для Cmd+V в любой раскладке) ----------

    [LibraryImport(CarbonLib)]
    public static partial nint TISCopyCurrentASCIICapableKeyboardLayoutInputSource();

    [LibraryImport(CarbonLib)]
    public static partial nint TISGetInputSourceProperty(nint source, nint key);

    [LibraryImport(CarbonLib)]
    public static partial byte LMGetKbdType();

    [LibraryImport(CarbonLib)]
    public static partial int UCKeyTranslate(nint layout, ushort virtualKeyCode, ushort keyAction, uint modifierKeyState,
        uint keyboardType, uint keyTranslateOptions, uint* deadKeyState, nuint maxStringLength, nuint* actualStringLength,
        char* unicodeString);

    public static nint CarbonConstant(string name) =>
        *(nint*)NativeLibrary.GetExport(NativeLibrary.Load(CarbonLib), name);
}
