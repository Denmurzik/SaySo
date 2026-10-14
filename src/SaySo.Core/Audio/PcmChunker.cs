namespace SaySo.Core.Audio;

/// <summary>Режет поток PCM на фрагменты фиксированного размера (по умолчанию 100 мс).</summary>
public sealed class PcmChunker
{
    private readonly byte[] _pending;
    private int _count;

    public PcmChunker(int chunkBytes = AudioFormat.ChunkBytes)
    {
        if (chunkBytes <= 0 || chunkBytes % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkBytes), "Размер фрагмента должен быть положительным и чётным.");
        }

        _pending = new byte[chunkBytes];
    }

    public void Append(ReadOnlySpan<byte> pcm, Action<byte[]> onChunk)
    {
        while (!pcm.IsEmpty)
        {
            var take = Math.Min(pcm.Length, _pending.Length - _count);
            pcm[..take].CopyTo(_pending.AsSpan(_count));
            _count += take;
            pcm = pcm[take..];
            if (_count == _pending.Length)
            {
                onChunk(_pending.ToArray());
                _count = 0;
            }
        }
    }

    /// <summary>Возвращает накопленный остаток (чётной длины, возможно пустой) и очищает его.</summary>
    public byte[] Flush()
    {
        var length = _count & ~1;
        var rest = _pending.AsSpan(0, length).ToArray();
        _count = 0;
        return rest;
    }
}
