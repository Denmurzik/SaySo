using SaySo.Core.Platform;
using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Settings;

/// <summary>Настройки и ключи, редактируемые окном; каждое сохранение уведомляет демон.</summary>
internal sealed class SettingsModel
{
    private readonly SettingsStore _settingsStore;
    private readonly SecretStore _secretStore;
    private bool _secretsDirty;

    public SettingsModel()
        : this(AppPaths.Default)
    {
    }

    public SettingsModel(AppPaths paths)
    {
        Paths = paths;
        paths.EnsureRoot();
        _settingsStore = new SettingsStore(paths);
        _secretStore = new SecretStore(paths);
        Settings = _settingsStore.Load();
        Secrets = _secretStore.Load();
        History = new HistoryStore(paths);
    }

    public AppPaths Paths { get; }

    public AppSettings Settings { get; }

    public Secrets Secrets { get; }

    public HistoryStore History { get; }

    public void SaveSettings()
    {
        _settingsStore.Save(Settings);
        IpcClient.Send(IpcProtocol.ReloadSettings);
    }

    public void SetGeminiKeys(IEnumerable<string?> keys)
    {
        Secrets.GeminiApiKeys = keys.Select(Clean).OfType<string>().ToList();
        _secretsDirty = true;
    }

    public void SetGroqKey(string? key)
    {
        Secrets.GroqApiKey = Clean(key);
        _secretsDirty = true;
    }

    public void SaveSecrets()
    {
        if (!_secretsDirty)
        {
            return;
        }

        _secretStore.Save(Secrets);
        _secretsDirty = false;
        IpcClient.Send(IpcProtocol.ReloadSettings);
    }

    private static string? Clean(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
