using SaySo.Core.Audio;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

public enum SoundKind
{
    Start,
    Stop,
    Error,
}

/// <summary>Короткие сигналы. PlaySound(SND_MEMORY|SND_ASYNC) читает буфер после возврата — он закреплён (POH).</summary>
public static unsafe class SoundPlayer
{
    private static readonly byte[] Start = Pin(SoundBank.Start);
    private static readonly byte[] Stop = Pin(SoundBank.Stop);
    private static readonly byte[] Error = Pin(SoundBank.Error);

    public static void Play(SoundKind kind)
    {
        var data = kind switch
        {
            SoundKind.Start => Start,
            SoundKind.Stop => Stop,
            _ => Error,
        };
        fixed (byte* p = data)
        {
            Native.PlaySoundW(p, 0, Native.SND_ASYNC | Native.SND_MEMORY | Native.SND_NODEFAULT);
        }
    }

    private static byte[] Pin(byte[] source)
    {
        var pinned = GC.AllocateArray<byte>(source.Length, pinned: true);
        source.CopyTo(pinned, 0);
        return pinned;
    }
}
