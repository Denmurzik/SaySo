using System.Globalization;

namespace SaySo.Core.Updates;

/// <summary>Последний релиз GitHub, как его видит SaySo.</summary>
/// <param name="Version">Версия X.Y.Z (ровно три компонента).</param>
/// <param name="Tag">Тег релиза как есть (vX.Y.Z).</param>
/// <param name="HtmlUrl">Страница релиза (https).</param>
/// <param name="Notes">Первые 500 символов описания релиза.</param>
/// <param name="SetupUrl">Ссылка на Setup.exe; null — такого ассета в релизе нет.</param>
/// <param name="SetupSize">Размер Setup.exe по данным GitHub.</param>
/// <param name="ShaUrl">Ссылка на .sha256; null — ассета нет.</param>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string HtmlUrl,
    string Notes,
    string? SetupUrl,
    long SetupSize,
    string? ShaUrl)
{
    /// <summary>Релиз можно поставить из приложения: есть и установщик, и его контрольная сумма.</summary>
    public bool IsInstallable => SetupUrl is not null && ShaUrl is not null && SetupSize > 0;

    /// <summary>Имя файла установщика; строится из разобранной версии, а не берётся из JSON.</summary>
    public string SetupFileName => SetupFileNameFor(Version);

    public string ShaFileName => SetupFileName + ".sha256";

    public static string SetupFileNameFor(Version version) => $"SaySo-Setup-{version.ToString(3)}.exe";
}

/// <summary>Разбор версий вида X.Y.Z.</summary>
public static class VersionTag
{
    /// <summary>
    /// «vX.Y.Z» или «X.Y.Z» — ровно три числа из цифр. Всё остальное («v1.2», «v1.2.3-beta», «v1.2.3.4», «latest») —
    /// не наша версия: такие теги игнорируются.
    /// </summary>
    public static bool TryParse(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrEmpty(tag))
        {
            return false;
        }

        var text = tag[0] is 'v' or 'V' ? tag[1..] : tag;
        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        Span<int> numbers = stackalloc int[3];
        for (var i = 0; i < 3; i++)
        {
            // NumberStyles.None: только цифры, без знака и пробелов; до 9 цифр — без переполнения int.
            if (parts[i].Length is 0 or > 9 || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        version = new Version(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>
    /// Версия сборки 0.1.0.0 и версия тега 0.1.0 — одно и то же: сравнение идёт по трём компонентам
    /// (у <see cref="Version"/> «0.1.0» и «0.1.0.0» не равны).
    /// </summary>
    public static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
