namespace SaySo.Core.Audio.Wasapi;

public enum MicrophoneErrorKind
{
    AccessDenied,
    NotFound,
    DeviceLost,
    InUse,
    Other,
}

/// <summary>Ошибка микрофона; <see cref="UserMessage"/> — для оверлея и настроек.</summary>
public sealed class MicrophoneException(MicrophoneErrorKind kind, int nativeError, string message) : Exception(message)
{
    public MicrophoneErrorKind Kind { get; } = kind;

    public int NativeError { get; } = nativeError;

    public string UserMessage => Kind switch
    {
        MicrophoneErrorKind.AccessDenied =>
            "Нет доступа к микрофону: Параметры → Конфиденциальность и защита → Микрофон → разрешите доступ классическим приложениям.",
        MicrophoneErrorKind.NotFound => "Микрофон не найден.",
        MicrophoneErrorKind.DeviceLost => "Микрофон отключился во время записи.",
        MicrophoneErrorKind.InUse => "Микрофон занят другим приложением.",
        _ => $"Ошибка микрофона (0x{NativeError:X8}).",
    };

    public static MicrophoneException FromHResult(int hr, string operation)
    {
        var kind = unchecked((uint)hr) switch
        {
            0x80070005 => MicrophoneErrorKind.AccessDenied,      // E_ACCESSDENIED
            0x80070490 => MicrophoneErrorKind.NotFound,          // E_NOTFOUND
            0x88890004 => MicrophoneErrorKind.DeviceLost,        // AUDCLNT_E_DEVICE_INVALIDATED
            0x8889000A => MicrophoneErrorKind.InUse,             // AUDCLNT_E_DEVICE_IN_USE
            _ => MicrophoneErrorKind.Other,
        };
        return new MicrophoneException(kind, hr, $"{operation}: 0x{hr:X8}");
    }
}
