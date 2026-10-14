using SaySo.Core.Platform;

namespace SaySo.Core.Audio;

/// <summary>
/// Читает звуки только выбранной темы и только по требованию (по одному виду за раз): в простое демон не держит ни байта
/// чужих тем. Буферы выделяются сразу закреплёнными (POH): PlaySound(SND_MEMORY|SND_ASYNC) читает память после возврата.
/// При смене темы прежние буферы отпускаются. Потоки не синхронизирует: вызывающий (SoundPlayer) сериализует доступ.
/// </summary>
public sealed class SoundThemeLoader(Func<string, Stream?> openResource)
{
    private const int MaxResourceBytes = 4 * 1024 * 1024;

    private readonly byte[]?[] _buffers = new byte[]?[3];

    public SoundThemeLoader()
        : this(OpenEmbedded)
    {
    }

    /// <summary>Выбранная тема; null, пока ничего не выбрано.</summary>
    public string? ThemeId { get; private set; }

    /// <summary>Загружен ли уже звук этого вида в выбранной теме.</summary>
    public bool IsLoaded(SoundKind kind) => _buffers[(int)kind] is not null;

    /// <returns>true — тема сменилась и буферы прежней отпущены; false — выбрана та же.</returns>
    public bool Select(string themeId)
    {
        if (string.Equals(ThemeId, themeId, StringComparison.Ordinal))
        {
            return false;
        }

        Array.Clear(_buffers);
        ThemeId = themeId;
        return true;
    }

    /// <summary>Закреплённый WAV выбранной темы; при первом обращении читается из ресурсов и запоминается.</summary>
    public byte[] Get(SoundKind kind)
    {
        var themeId = ThemeId ?? throw new InvalidOperationException("Звуковая тема не выбрана.");
        if (_buffers[(int)kind] is { } cached)
        {
            return cached;
        }

        var name = SoundThemes.ResourceName(themeId, kind);
        using var stream = openResource(name) ?? throw new InvalidOperationException($"В сборке нет ресурса {name}.");
        if (stream.Length is <= 0 or > MaxResourceBytes)
        {
            throw new InvalidDataException($"Ресурс {name} имеет недопустимый размер.");
        }

        var data = GC.AllocateUninitializedArray<byte>((int)stream.Length, pinned: true);
        stream.ReadExactly(data);
        _buffers[(int)kind] = data;
        return data;
    }

    private static Stream? OpenEmbedded(string name) => typeof(SoundThemeLoader).Assembly.GetManifestResourceStream(name);
}
