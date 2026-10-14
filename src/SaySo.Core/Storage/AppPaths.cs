namespace SaySo.Core.Storage;

/// <summary>Пути к файлам данных SaySo.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    /// <summary>Только для разработки: абсолютный путь в этой переменной окружения заменяет %LOCALAPPDATA%\SaySo.</summary>
    public const string DataDirVariable = "SAYSO_DATA_DIR";

    /// <summary>%LOCALAPPDATA%\SaySo, а при заданной SAYSO_DATA_DIR (абсолютный путь) этот каталог.</summary>
    public static AppPaths Default { get; } = Resolve(
        Environment.GetEnvironmentVariable(DataDirVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>Пустое значение и относительный путь игнорируются: данные не должны оказаться в случайном рабочем каталоге.</summary>
    public static AppPaths Resolve(string? dataDirOverride, string localAppData)
    {
        var custom = dataDirOverride?.Trim();
        return !string.IsNullOrEmpty(custom) && Path.IsPathFullyQualified(custom)
            ? new AppPaths(custom)
            : new AppPaths(Path.Combine(localAppData, "SaySo"));
    }

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SecretsFile => Path.Combine(Root, "secrets.bin");

    public string HistoryFile => Path.Combine(Root, "history.bin");

    public string UpdateStateFile => Path.Combine(Root, "update-state.json");

    public void EnsureRoot() => Directory.CreateDirectory(Root);
}
