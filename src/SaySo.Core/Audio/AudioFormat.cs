namespace SaySo.Core.Audio;

/// <summary>Единый формат аудио SaySo: PCM 16 бит, 16 кГц, моно, little-endian.</summary>
public static class AudioFormat
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BytesPerSample = 2;
    public const int BytesPerSecond = SampleRate * BytesPerSample;
    public const int ChunkMilliseconds = 100;
    public const int ChunkBytes = BytesPerSecond * ChunkMilliseconds / 1000;

    /// <summary>Лимит сессии Gemini Live — 10 минут.</summary>
    public const int MaxUtteranceSeconds = 600;

    /// <summary>Автозавершение записи с запасом до лимита.</summary>
    public const int AutoStopSeconds = 570;

    public static int BytesToMilliseconds(long bytes) => (int)(bytes * 1000 / BytesPerSecond);
}
