namespace SaySo.Core.Audio;

/// <summary>
/// Полная запись реплики (нужна резервному движку). Хранится сегментами по 1 с (32 000 байт),
/// чтобы не было больших перевыделений и объектов в LOH.
/// </summary>
public sealed class UtteranceBuffer
{
    private const int SegmentBytes = AudioFormat.BytesPerSecond;
    private readonly List<byte[]> _segments = [];
    private int _lastUsed = SegmentBytes;

    public UtteranceBuffer(long maxBytes = (long)AudioFormat.BytesPerSecond * AudioFormat.MaxUtteranceSeconds)
    {
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }

    public long Length { get; private set; }

    public bool IsFull => Length >= MaxBytes;

    /// <returns>Сколько байт принято (остаток сверх лимита отбрасывается).</returns>
    public int Append(ReadOnlySpan<byte> pcm)
    {
        var allowed = (int)Math.Min(pcm.Length, MaxBytes - Length);
        var src = pcm[..allowed];
        while (!src.IsEmpty)
        {
            if (_lastUsed == SegmentBytes)
            {
                _segments.Add(new byte[SegmentBytes]);
                _lastUsed = 0;
            }

            var take = Math.Min(src.Length, SegmentBytes - _lastUsed);
            src[..take].CopyTo(_segments[^1].AsSpan(_lastUsed));
            _lastUsed += take;
            src = src[take..];
        }

        Length += allowed;
        return allowed;
    }

    /// <summary>Копирует первые <c>destination.Length</c> байт записи.</summary>
    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length > Length)
        {
            throw new ArgumentException("Запрошено больше, чем записано.", nameof(destination));
        }

        var offset = 0;
        for (var i = 0; i < _segments.Count && offset < destination.Length; i++)
        {
            var used = i == _segments.Count - 1 ? _lastUsed : SegmentBytes;
            var count = Math.Min(used, destination.Length - offset);
            _segments[i].AsSpan(0, count).CopyTo(destination[offset..]);
            offset += count;
        }
    }

    public void Clear()
    {
        _segments.Clear();
        _segments.TrimExcess();
        _lastUsed = SegmentBytes;
        Length = 0;
    }
}
