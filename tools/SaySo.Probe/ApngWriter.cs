using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SaySo.Probe;

/// <summary>Минимальный писатель анимированного PNG (APNG, RGBA 8 бит). GitHub показывает APNG анимированным.</summary>
internal static class ApngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <param name="frames">Кадры в формате BGRA с предумноженной альфой (как у OverlayRenderer).</param>
    public static void Write(string path, int width, int height, IReadOnlyList<uint[]> frames, int delayMs)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // бит на канал
        ihdr[9] = 6; // RGBA
        Chunk(fs, "IHDR", ihdr);

        var actl = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl, frames.Count);
        Chunk(fs, "acTL", actl); // num_plays = 0 — бесконечно

        var sequence = 0;
        for (var i = 0; i < frames.Count; i++)
        {
            var fctl = new byte[26];
            BinaryPrimitives.WriteInt32BigEndian(fctl, sequence++);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), width);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), height);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), (ushort)delayMs);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), 1000);
            fctl[24] = 1; // dispose: очистить до прозрачного
            fctl[25] = 0; // blend: заменить
            Chunk(fs, "fcTL", fctl);

            var data = Compress(frames[i], width, height);
            if (i == 0)
            {
                Chunk(fs, "IDAT", data);
            }
            else
            {
                var fdat = new byte[4 + data.Length];
                BinaryPrimitives.WriteInt32BigEndian(fdat, sequence++);
                data.CopyTo(fdat, 4);
                Chunk(fs, "fdAT", fdat);
            }
        }

        Chunk(fs, "IEND", []);
    }

    private static byte[] Compress(uint[] px, int w, int h)
    {
        var raw = new byte[h * (1 + w * 4)];
        var o = 0;
        for (var y = 0; y < h; y++)
        {
            raw[o++] = 0; // фильтр строки: None
            for (var x = 0; x < w; x++)
            {
                var p = px[y * w + x];
                var a = p >> 24;
                raw[o++] = Unpremultiply(p >> 16, a);
                raw[o++] = Unpremultiply(p >> 8, a);
                raw[o++] = Unpremultiply(p, a);
                raw[o++] = (byte)a;
            }
        }

        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            z.Write(raw);
        }

        return ms.ToArray();
    }

    private static byte Unpremultiply(uint channel, uint alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, ((channel & 0xFF) * 255 + alpha / 2) / alpha);

    private static void Chunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        s.Write(word);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(word, Crc32(typeBytes, data));
        s.Write(word);
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var c = 0xFFFFFFFFu;
        foreach (var x in a)
        {
            c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        }

        foreach (var x in b)
        {
            c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        }

        return c ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
