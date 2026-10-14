namespace SaySo.Core.Storage;

/// <summary>Пути к файлам данных SaySo.</summary>
public sealed class AppPaths
{
    public AppPaths(string root)
    {
        Root = root;
    }

    /// <summary>%LOCALAPPDATA%\SaySo</summary>
    public static AppPaths Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SaySo"));

    public string Root { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string SecretsFile => Path.Combine(Root, "secrets.bin");

    public string HistoryFile => Path.Combine(Root, "history.bin");

    public string UpdateStateFile => Path.Combine(Root, "update-state.json");

    public void EnsureRoot() => Directory.CreateDirectory(Root);
}
