using SaySo.Core.Audio;

namespace SaySo.Core.Tests.Audio;

public class UtteranceBufferTests
{
    [Fact]
    public void Append_AcrossSegments_PreservesBytes()
    {
        var buffer = new UtteranceBuffer();
        var data = new byte[AudioFormat.BytesPerSecond * 2 + 123];
        Random.Shared.NextBytes(data);
        buffer.Append(data.AsSpan(0, 1000));
        buffer.Append(data.AsSpan(1000));

        Assert.Equal(data.Length, buffer.Length);
        var copy = new byte[buffer.Length];
        buffer.CopyTo(copy);
        Assert.Equal(data, copy);
    }

    [Fact]
    public void Append_BeyondMax_IsTruncated()
    {
        var buffer = new UtteranceBuffer(maxBytes: 10);
        Assert.Equal(6, buffer.Append(new byte[6]));
        Assert.Equal(4, buffer.Append(new byte[6]));
        Assert.Equal(0, buffer.Append(new byte[6]));
        Assert.True(buffer.IsFull);
        Assert.Equal(10, buffer.Length);
    }

    [Fact]
    public void Clear_ResetsLength()
    {
        var buffer = new UtteranceBuffer();
        buffer.Append(new byte[5000]);
        buffer.Clear();
        Assert.Equal(0, buffer.Length);
        buffer.Append(new byte[] { 7, 8 });
        var copy = new byte[2];
        buffer.CopyTo(copy);
        Assert.Equal(new byte[] { 7, 8 }, copy);
    }

    [Fact]
    public void CopyTo_Prefix_CopiesFirstBytesAcrossSegments()
    {
        var buffer = new UtteranceBuffer();
        var data = new byte[AudioFormat.BytesPerSecond + 5000];
        Random.Shared.NextBytes(data);
        buffer.Append(data);
        var prefix = new byte[AudioFormat.BytesPerSecond + 100];
        buffer.CopyTo(prefix);
        Assert.Equal(data.AsSpan(0, prefix.Length).ToArray(), prefix);
        Assert.Throws<ArgumentException>(() => buffer.CopyTo(new byte[data.Length + 1]));
    }

    [Fact]
    public void DefaultMax_Is600Seconds() =>
        Assert.Equal(600L * 32000, new UtteranceBuffer().MaxBytes);
}
