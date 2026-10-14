using System.Text.Json;
using SaySo.Core.Json;
using SaySo.Core.Storage;

namespace SaySo.Core.Settings;

/// <summary>Чтение и атомарная запись settings.json.</summary>
public sealed class SettingsStore(AppPaths paths)
{
    public AppSettings Load()
    {
        TryLoad(out var settings);
        return settings;
    }

    /// <summary>
    /// Читает настройки. <c>false</c> — settings.json существует, но прочитать его не удалось (блокировка, права):
    /// <paramref name="settings"/> тогда значения по умолчанию, и тот, у кого уже есть рабочие настройки (демон при
    /// ReloadSettings), должен оставить их, а не откатываться на умолчания. Нет файла или он повреждён — <c>true</c>.
    /// </summary>
    public bool TryLoad(out AppSettings settings)
    {
        var path = paths.SettingsFile;
        var status = StoreFile.Read(path, "SettingsStore", out var bytes);
        if (bytes is null)
        {
            settings = SettingsNormalizer.Normalize(new AppSettings());
            return status != FileReadStatus.Failed;
        }

        try
        {
            settings = SettingsNormalizer.Normalize(JsonSerializer.Deserialize(bytes, SaySoJsonContext.Default.AppSettings) ?? new AppSettings());
        }
        catch (JsonException e)
        {
            // Повреждённый файл: сохраняем копию для диагностики и работаем с настройками по умолчанию.
            StoreFile.Quarantine(path, "SettingsStore", e);
            settings = SettingsNormalizer.Normalize(new AppSettings());
        }

        return true;
    }

    public void Save(AppSettings settings)
    {
        var normalized = SettingsNormalizer.Normalize(settings);
        AtomicFile.WriteAllBytes(paths.SettingsFile,
            JsonSerializer.SerializeToUtf8Bytes(normalized, SaySoJsonContext.Default.AppSettings));
    }
}
