using SaySo.Core.Overlay;

namespace SaySo.Core.Audio.Wasapi;

public enum MicrophoneErrorKind
{
    AccessDenied,
    NotFound,
    DeviceLost,
    InUse,
    Other,
}

/// <summary>
/// Ошибка микрофона. <see cref="UserMessage"/> — короткий текст для плашки оверлея, <see cref="Detail"/> — полный
/// (подсказка трея и страница «Микрофон»).
/// </summary>
public sealed class MicrophoneException(MicrophoneErrorKind kind, int nativeError, string message) : Exception(message)
{
    public MicrophoneErrorKind Kind { get; } = kind;

    public int NativeError { get; } = nativeError;

    public string UserMessage => Text.Short;

    public string Detail => Text.Detail;

    private FailureText Text => OverlayMessages.Microphone(Kind, NativeError);

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
