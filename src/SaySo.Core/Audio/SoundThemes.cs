using SaySo.Core.Platform;

namespace SaySo.Core.Audio;

/// <summary>Звуковая тема: идентификатор (он же имя каталога ресурсов) и название для окна настроек.</summary>
public sealed record SoundTheme(string Id, string Name);

/// <summary>
/// Каталог звуковых тем. Звуки встроены в SaySo.Core как ресурсы SaySo.Sounds.&lt;тема&gt;.&lt;вид&gt;.wav
/// (48 кГц, моно, 16 бит); их создаёт tools/sounds/generate_sounds.py.
/// </summary>
public static class SoundThemes
{
    public const string DefaultId = "5-voylok";

    /// <summary>Темы в порядке показа в окне настроек.</summary>
    public static IReadOnlyList<SoundTheme> All { get; } =
    [
        new("1-steklo", "Стекло"),
        new("2-kaplya", "Капля"),
        new("3-derevo", "Дерево"),
        new("4-kolokolchik", "Колокольчик"),
        new("5-voylok", "Войлок"),
        new("6-podushka", "Подушка"),
        new("7-tap", "Тап"),
        new("8-kalimba", "Калимба"),
    ];

    public static bool IsKnown(string? id)
    {
        foreach (var theme in All)
        {
            if (string.Equals(theme.Id, id, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Неизвестный, пустой или повреждённый идентификатор (ручная правка JSON) заменяется темой по умолчанию.</summary>
    public static string Normalize(string? id) => IsKnown(id) ? id! : DefaultId;

    public static string ResourceName(string themeId, SoundKind kind) => $"SaySo.Sounds.{themeId}.{FileName(kind)}.wav";

    private static string FileName(SoundKind kind) => kind switch
    {
        SoundKind.Start => "start",
        SoundKind.Stop => "stop",
        SoundKind.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
