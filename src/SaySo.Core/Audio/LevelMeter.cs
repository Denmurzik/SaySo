using System.Runtime.InteropServices;

namespace SaySo.Core.Audio;

/// <summary>Уровень громкости для индикатора: RMS в dBFS, −60 dB → 0, 0 dB → 1.</summary>
public static class LevelMeter
{
    private const double FloorDb = -60.0;

    public static float Compute(ReadOnlySpan<byte> pcm16)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcm16);
        if (samples.IsEmpty)
        {
            return 0f;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += (double)s * s;
        }

        var rms = Math.Sqrt(sum / samples.Length) / 32768.0;
        if (rms <= 0)
        {
            return 0f;
        }

        var db = 20 * Math.Log10(rms);
        return (float)Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }
}
