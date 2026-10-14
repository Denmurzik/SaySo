using System.Buffers.Binary;
using System.Text;
using SaySo.Core.Audio;

namespace SaySo.Core.Tests.Audio;

public class WavWriterTests
{
    [Fact]
    public void Create_WritesCanonicalPcmHeader()
    {
        var pcm = new byte[] { 1, 0, 2, 0, 3, 0 };
        var wav = WavWriter.Create(pcm);

        Assert.Equal(44 + 6, wav.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal(36 + 6, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20)));      // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));      // моно
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
        Assert.Equal(6, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public void Create_FromUtteranceBuffer_MatchesSpanOverload()
    {
        var data = new byte[70000];
        Random.Shared.NextBytes(data);
        var buffer = new UtteranceBuffer();
        buffer.Append(data);
        Assert.Equal(WavWriter.Create(data), WavWriter.Create(buffer));
    }
}
