using System.Buffers.Binary;

namespace SaySo.Core.Audio;

/// <summary>Сборка WAV (RIFF PCM) в памяти в формате <see cref="AudioFormat"/>.</summary>
public static class WavWriter
{
    public const int HeaderBytes = 44;

    public static byte[] Create(ReadOnlySpan<byte> pcm)
    {
        var wav = new byte[HeaderBytes + pcm.Length];
        WriteHeader(wav, pcm.Length);
        pcm.CopyTo(wav.AsSpan(HeaderBytes));
        return wav;
    }

    public static byte[] Create(UtteranceBuffer pcm)
    {
        var length = checked((int)pcm.Length);
        var wav = new byte[HeaderBytes + length];
        WriteHeader(wav, length);
        pcm.CopyTo(wav.AsSpan(HeaderBytes));
        return wav;
    }

    public static void WriteHeader(Span<byte> d, int dataLength)
    {
        "RIFF"u8.CopyTo(d);
        BinaryPrimitives.WriteInt32LittleEndian(d[4..], 36 + dataLength);
        "WAVE"u8.CopyTo(d[8..]);
        "fmt "u8.CopyTo(d[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(d[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(d[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(d[22..], AudioFormat.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(d[24..], AudioFormat.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(d[28..], AudioFormat.BytesPerSecond);
        BinaryPrimitives.WriteInt16LittleEndian(d[32..], AudioFormat.Channels * AudioFormat.BytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(d[34..], AudioFormat.BitsPerSample);
        "data"u8.CopyTo(d[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(d[40..], dataLength);
    }
}
