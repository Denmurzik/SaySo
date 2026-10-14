using System.Security.Cryptography;
using SaySo.Core.Diagnostics;
using SaySo.Core.Platform;
using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Settings;

/// <summary>Настройки и ключи, редактируемые окном; каждое сохранение уведомляет демон.</summary>
internal sealed class SettingsModel
{
    /// <summary>Текст для окна, когда файл настроек не удалось записать (занят антивирусом/другим процессом).</summary>
    public const string SaveFailedMessage = "Не удалось сохранить: файл занят, попробуйте ещё раз.";

    private readonly SettingsStore _settingsStore;
    private readonly SecretStore _secretStore;
    private bool _settingsDirty;
    private bool _secretsDirty;

    public SettingsModel()
        : this(AppPaths.Default)
    {
    }

    public SettingsModel(AppPaths paths)
    {
        Paths = paths;
        paths.EnsureRoot();
        Log.FilePath ??= Path.Combine(paths.Root, "sayso.log");
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

    /// <summary>Есть изменения, ещё не записанные на диск (значение уже в <see cref="Settings"/>).</summary>
    public bool HasUnsavedChanges => _settingsDirty || _secretsDirty;

    /// <summary>Отметить настройки изменёнными без записи: запись — при потере фокуса, переходе или закрытии окна.</summary>
    public void MarkSettingsDirty() => _settingsDirty = true;

    /// <returns>false — файл не удалось записать (причина в журнале); изменения остаются «несохранёнными».</returns>
    public bool SaveSettings()
    {
        _settingsDirty = true;
        try
        {
            _settingsStore.Save(Settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Error("Сохранение настроек", e);
            return false;
        }

        _settingsDirty = false;
        IpcClient.Send(IpcProtocol.ReloadSettings);
        return true;
    }

    /// <summary>Разделители ключей в одной строке (вставка «k1, k2» или ключей с новой строки).</summary>
    private static readonly char[] KeySeparators = [',', ';', ' ', '\t', '\r', '\n'];

    /// <summary>Разбить строку на отдельные ключи (пустой результат — ключей нет).</summary>
    public static string[] SplitKeys(string? text) =>
        (text ?? "").Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Запомнить ключи Gemini; строки вида «k1, k2» разбиваются. Изменений нет — модель не помечается несохранённой.</summary>
    public void SetGeminiKeys(IEnumerable<string?> keys)
    {
        var cleaned = keys.SelectMany(SplitKeys).ToList();
        if (cleaned.SequenceEqual(Secrets.GeminiApiKeys))
        {
            return;
        }

        Secrets.GeminiApiKeys = cleaned;
        _secretsDirty = true;
    }

    public void SetGroqKey(string? key)
    {
        var cleaned = Clean(key);
        if (cleaned == Secrets.GroqApiKey)
        {
            return;
        }

        Secrets.GroqApiKey = cleaned;
        _secretsDirty = true;
    }

    /// <returns>false — файл ключей не удалось записать; true — записано или записывать было нечего.</returns>
    public bool SaveSecrets()
    {
        if (!_secretsDirty)
        {
            return true;
        }

        try
        {
            _secretStore.Save(Secrets);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException)
        {
            Log.Error("Сохранение ключей", e);
            return false;
        }

        _secretsDirty = false;
        IpcClient.Send(IpcProtocol.ReloadSettings);
        return true;
    }

    /// <summary>Записать всё несохранённое (переход между страницами, закрытие окна).</summary>
    public bool Flush()
    {
        var ok = SaveSecrets();
        if (_settingsDirty)
        {
            ok &= SaveSettings();
        }

        return ok;
    }

    private static string? Clean(string? key) => string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
