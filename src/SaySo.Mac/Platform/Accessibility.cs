using System.Runtime.InteropServices;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>«Универсальный доступ» (Accessibility): нужен перехвату горячей клавиши и синтетическому Cmd+V.</summary>
internal static unsafe class Accessibility
{
    public const string SettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility";
    public const string MicrophoneSettingsUrl = "x-apple.systempreferences:com.apple.preference.security?Privacy_Microphone";

    public static bool IsTrusted => Quartz.AXIsProcessTrusted() != 0;

    /// <summary>Системный диалог «SaySo хочет управлять компьютером…» с кнопкой перехода в Системные настройки.</summary>
    public static void Prompt()
    {
        using var pool = AutoreleasePool.Push();
        var lib = NativeLibrary.Load(Quartz.ApplicationServicesLib);
        var key = *(nint*)NativeLibrary.GetExport(lib, "kAXTrustedCheckOptionPrompt");
        var yes = ObjC.Send(ObjC.Class("NSNumber"), ObjC.Sel("numberWithBool:"), true);
        var options = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Sel("dictionaryWithObject:forKey:"), yes, key);
        Quartz.AXIsProcessTrustedWithOptions(options);
    }
}
