using System.Buffers.Binary;
using System.Text;
using SaySo.Core.Audio;

namespace SaySo.Core.Tests.Audio;

public class WavReaderTests
{
    [Fact]
    public void ReadPcm_RoundTripsWavWriter()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6 };
        Assert.Equal(pcm, WavReader.ReadPcm(WavWriter.Create(pcm)));
    }

    [Fact]
    public void ReadPcm_SkipsUnknownChunks()
    {
        // RIFF + fmt + LIST (лишний блок, как пишет системный TTS) + data
        var pcm = new byte[] { 9, 8, 7, 6 };
        using var ms = new MemoryStream();
        var header = WavWriter.Create(pcm);
        ms.Write(header, 0, 36);                       // RIFF..fmt
        ms.Write(Encoding.ASCII.GetBytes("LIST"));
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, 3);
        ms.Write(size);
        ms.Write(new byte[] { 1, 2, 3, 0 });           // 3 байта + выравнивание
        ms.Write(header, 36, header.Length - 36);      // data
        Assert.Equal(pcm, WavReader.ReadPcm(ms.ToArray()));
    }

    [Fact]
    public void ReadPcm_WrongFormat_Throws()
    {
        var wav = WavWriter.Create(new byte[4]);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 44100);
        Assert.Throws<InvalidDataException>(() => WavReader.ReadPcm(wav));
    }

    [Fact]
    public void ReadPcm_NotWav_Throws() =>
        Assert.Throws<InvalidDataException>(() => WavReader.ReadPcm(Encoding.ASCII.GetBytes("это не wav, а текст достаточной длины для заголовка")));
}
