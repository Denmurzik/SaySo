using System.Text.Json;
using SaySo.Core.Json;
using SaySo.Core.Storage;

namespace SaySo.Core.Settings;

/// <summary>Чтение и атомарная запись settings.json.</summary>
public sealed class SettingsStore(AppPaths paths)
{
    public AppSettings Load()
    {
        var path = paths.SettingsFile;
        if (!File.Exists(path))
        {
            return SettingsNormalizer.Normalize(new AppSettings());
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            var settings = JsonSerializer.Deserialize(bytes, SaySoJsonContext.Default.AppSettings) ?? new AppSettings();
            return SettingsNormalizer.Normalize(settings);
        }
        catch (JsonException)
        {
            // Повреждённый файл: сохраняем копию для диагностики и работаем с настройками по умолчанию.
            try { File.Copy(path, path + ".bad", overwrite: true); } catch (IOException) { }
            return SettingsNormalizer.Normalize(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        var normalized = SettingsNormalizer.Normalize(settings);
        AtomicFile.WriteAllBytes(paths.SettingsFile,
            JsonSerializer.SerializeToUtf8Bytes(normalized, SaySoJsonContext.Default.AppSettings));
    }
}
