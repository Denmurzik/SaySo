using System.Buffers.Binary;

namespace SaySo.Core.Audio;

/// <summary>Чтение PCM из WAV (для Probe и режима --simulate). Формат обязан совпадать с <see cref="AudioFormat"/>.</summary>
public static class WavReader
{
    public static byte[] ReadPcm(string path) => ReadPcm(File.ReadAllBytes(path));

    public static byte[] ReadPcm(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Это не WAV-файл.");
        }

        int? channels = null, rate = null, bits = null;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav[(offset + 4)..]);
            var body = offset + 8;
            if (size < 0 || body > wav.Length)
            {
                break;
            }

            if (id.SequenceEqual("fmt "u8) && size >= 16)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(wav[(body + 2)..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(wav[(body + 4)..]);
                bits = BinaryPrimitives.ReadInt16LittleEndian(wav[(body + 14)..]);
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (channels != AudioFormat.Channels || rate != AudioFormat.SampleRate || bits != AudioFormat.BitsPerSample)
                {
                    throw new InvalidDataException(
                        $"Нужен WAV 16000 Гц / 16 бит / моно, а не {rate} Гц / {bits} бит / {channels} кан.");
                }

                return wav.Slice(body, Math.Min(size, wav.Length - body)).ToArray();
            }

            offset = body + size + (size & 1);
        }

        throw new InvalidDataException("В WAV нет блока data.");
    }
}
