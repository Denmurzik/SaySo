using System.Buffers.Binary;

namespace SaySo.Core.Audio;

/// <summary>Генерация коротких звуковых сигналов (без файлов-ресурсов).</summary>
public static class ToneGenerator
{
    private const int FadeSamples = 80; // 5 мс атаки и затухания — без щелчков

    public static byte[] CreateWav(ReadOnlySpan<(int FrequencyHz, int DurationMs)> notes, float volume = 0.25f)
    {
        var totalSamples = 0;
        foreach (var n in notes)
        {
            totalSamples += n.DurationMs * AudioFormat.SampleRate / 1000;
        }

        var wav = new byte[WavWriter.HeaderBytes + totalSamples * 2];
        WavWriter.WriteHeader(wav, totalSamples * 2);
        var offset = WavWriter.HeaderBytes;
        foreach (var n in notes)
        {
            var count = n.DurationMs * AudioFormat.SampleRate / 1000;
            for (var i = 0; i < count; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, count - 1 - i) / (double)FadeSamples);
                var v = volume * envelope * Math.Sin(2 * Math.PI * n.FrequencyHz * i / AudioFormat.SampleRate);
                BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(offset), (short)(v * 32767));
                offset += 2;
            }
        }

        return wav;
    }
}

/// <summary>Готовые сигналы SaySo: начало, конец, ошибка.</summary>
public static class SoundBank
{
    private static readonly Lazy<byte[]> StartWav = new(() => ToneGenerator.CreateWav([(660, 60), (880, 90)], 0.2f));
    private static readonly Lazy<byte[]> StopWav = new(() => ToneGenerator.CreateWav([(880, 60), (660, 90)], 0.2f));
    private static readonly Lazy<byte[]> ErrorWav = new(() => ToneGenerator.CreateWav([(330, 120), (220, 180)], 0.25f));

    public static byte[] Start => StartWav.Value;

    public static byte[] Stop => StopWav.Value;

    public static byte[] Error => ErrorWav.Value;
}
