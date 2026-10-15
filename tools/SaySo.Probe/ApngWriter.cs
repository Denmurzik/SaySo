using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SaySo.Probe;

/// <summary>Минимальный писатель анимированного PNG (APNG, RGBA 8 бит). GitHub показывает APNG анимированным.</summary>
internal static class ApngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Обычный (неанимированный) PNG RGBA 8 бит из одного кадра BGRA с предумноженной альфой.</summary>
    public static void WriteStill(string path, int width, int height, uint[] pixels)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // бит на канал
        ihdr[9] = 6; // RGBA
        Chunk(fs, "IHDR", ihdr);
        Chunk(fs, "IDAT", Compress(pixels, width, height));
        Chunk(fs, "IEND", []);
    }

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

    /// <summary>
    /// Запись экрана: непрозрачные кадры BGRA с разными задержками. Каждый кадр после первого хранит только прямоугольник,
    /// изменившийся с предыдущего (dispose: оставить, blend: заменить), а одинаковые кадры склеиваются в один длинный —
    /// неподвижный фон не повторяется, и файл остаётся маленьким.
    /// </summary>
    public static void WriteTimed(string path, int width, int height, IReadOnlyList<uint[]> frames, IReadOnlyList<int> delaysMs)
    {
        // Склейка одинаковых соседних кадров.
        var kept = new List<(uint[] Pixels, int Delay)>();
        for (var i = 0; i < frames.Count; i++)
        {
            if (kept.Count > 0 && kept[^1].Pixels.AsSpan().SequenceEqual(frames[i]))
            {
                kept[^1] = (kept[^1].Pixels, kept[^1].Delay + delaysMs[i]);
            }
            else
            {
                kept.Add((frames[i], delaysMs[i]));
            }
        }

        using var fs = File.Create(path);
        fs.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        Chunk(fs, "IHDR", ihdr);
        var actl = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl, kept.Count);
        Chunk(fs, "acTL", actl);

        var sequence = 0;
        for (var i = 0; i < kept.Count; i++)
        {
            var (x0, y0, x1, y1) = i == 0 ? (0, 0, width, height) : Changed(kept[i - 1].Pixels, kept[i].Pixels, width, height);
            var w = x1 - x0;
            var h = y1 - y0;
            var crop = new uint[w * h];
            for (var y = 0; y < h; y++)
            {
                kept[i].Pixels.AsSpan((y0 + y) * width + x0, w).CopyTo(crop.AsSpan(y * w, w));
            }

            var fctl = new byte[26];
            BinaryPrimitives.WriteInt32BigEndian(fctl, sequence++);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), w);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), h);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(12), x0);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(16), y0);
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(20), (ushort)Math.Clamp(kept[i].Delay, 1, 65535));
            BinaryPrimitives.WriteUInt16BigEndian(fctl.AsSpan(22), 1000);
            fctl[24] = 0; // dispose: оставить кадр как есть
            fctl[25] = 0; // blend: заменить прямоугольник
            Chunk(fs, "fcTL", fctl);

            var data = Compress(crop, w, h);
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

    /// <summary>Прямоугольник [x0, x1) × [y0, y1), в котором кадры различаются (не пустой).</summary>
    private static (int X0, int Y0, int X1, int Y1) Changed(uint[] a, uint[] b, int width, int height)
    {
        int x0 = width, y0 = height, x1 = 0, y1 = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (a[y * width + x] != b[y * width + x])
                {
                    x0 = Math.Min(x0, x);
                    y0 = Math.Min(y0, y);
                    x1 = Math.Max(x1, x + 1);
                    y1 = Math.Max(y1, y + 1);
                }
            }
        }

        return x1 == 0 ? (0, 0, 1, 1) : (x0, y0, x1, y1);
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
