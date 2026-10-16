using System.Runtime.InteropServices;
using SaySo.Core.Diagnostics;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>
/// Разрешение macOS на микрофон (TCC). Без него Core Audio не отказывает, а молча отдаёт тишину, поэтому состояние
/// проверяется до записи. Запрос показывает системный диалог один раз; ответ приходит в блок Objective-C — он собран
/// вручную как глобальный блок (<see cref="Blocks"/>), так что ни Xcode, ни ARC не нужны.
/// </summary>
internal static unsafe class MicrophonePermission
{
    private const string AvFoundationLib = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

    private static readonly object Gate = new();
    private static nint _block;
    private static TaskCompletionSource<bool>? _pending;

    public enum Status
    {
        NotDetermined = 0,
        Restricted = 1,
        Denied = 2,
        Authorized = 3,
    }

    public static Status Current()
    {
        try
        {
            var mediaType = MediaTypeAudio(); // заодно загружает AVFoundation: без неё класса AVCaptureDevice в процессе нет
            return (Status)ObjC.SendLong(ObjC.Class("AVCaptureDevice"), ObjC.Sel("authorizationStatusForMediaType:"), mediaType);
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Error("Разрешение на микрофон", e);
            return Status.Authorized; // не смогли спросить систему: пусть решит сама запись
        }
    }

    /// <summary>Показывает системный запрос (если ответа ещё не было) и ждёт решения пользователя.</summary>
    public static Task<bool> RequestAsync()
    {
        lock (Gate)
        {
            if (_pending is { } pending)
            {
                return pending.Task;
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending = tcs;
            try
            {
                var mediaType = MediaTypeAudio();
                ObjC.SendVoid(ObjC.Class("AVCaptureDevice"), ObjC.Sel("requestAccessForMediaType:completionHandler:"), mediaType, Block());
            }
            catch (Exception e)
            {
                _pending = null;
                Log.Error("Запрос разрешения на микрофон", e);
                tcs.TrySetResult(false);
            }

            return tcs.Task;
        }
    }

    private static nint MediaTypeAudio()
    {
        var lib = NativeLibrary.Load(AvFoundationLib);
        return *(nint*)NativeLibrary.GetExport(lib, "AVMediaTypeAudio");
    }

    /// <summary>Глобальный блок <c>^(BOOL granted)</c>.</summary>
    private static nint Block() =>
        _block != 0 ? _block : _block = Blocks.Global((nint)(delegate* unmanaged<nint, byte, void>)&OnAnswer);

    [UnmanagedCallersOnly]
    private static void OnAnswer(nint block, byte granted)
    {
        TaskCompletionSource<bool>? pending;
        lock (Gate)
        {
            pending = _pending;
            _pending = null;
        }

        Log.Info(granted != 0 ? "Доступ к микрофону разрешён" : "Доступ к микрофону не разрешён");
        pending?.TrySetResult(granted != 0);
    }
}
