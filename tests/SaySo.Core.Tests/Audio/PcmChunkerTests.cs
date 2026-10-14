using SaySo.Core.Audio;

namespace SaySo.Core.Tests.Audio;

public class PcmChunkerTests
{
    [Fact]
    public void Append_EmitsFixedSizeChunks_InOrder()
    {
        var chunker = new PcmChunker(4);
        var chunks = new List<byte[]>();
        chunker.Append([1, 2, 3], chunks.Add);
        chunker.Append([4, 5, 6, 7, 8, 9, 10], chunks.Add);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, chunks[0]);
        Assert.Equal(new byte[] { 5, 6, 7, 8 }, chunks[1]);
        Assert.Equal(new byte[] { 9, 10 }, chunker.Flush());
        Assert.Empty(chunker.Flush());
    }

    [Fact]
    public void Flush_DropsOddTrailingByte()
    {
        var chunker = new PcmChunker(4);
        chunker.Append([1, 2, 3], _ => { });
        Assert.Equal(new byte[] { 1, 2 }, chunker.Flush());
    }

    [Fact]
    public void EmittedChunks_AreIndependentCopies()
    {
        var chunker = new PcmChunker(2);
        var chunks = new List<byte[]>();
        chunker.Append([1, 2, 3, 4], chunks.Add);
        Assert.Equal(new byte[] { 1, 2 }, chunks[0]);
        Assert.Equal(new byte[] { 3, 4 }, chunks[1]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Ctor_InvalidChunkSize_Throws(int size) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmChunker(size));

    [Fact]
    public void DefaultChunk_Is100msOf16kMono16bit() => Assert.Equal(3200, AudioFormat.ChunkBytes);
}
