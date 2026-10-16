using System.Security.Cryptography;
using System.Text.Json;
using SaySo.Core.Diagnostics;
using SaySo.Core.Json;

namespace SaySo.Core.Storage;

/// <summary>Одна диктовка в истории.</summary>
public sealed record HistoryEntry(DateTimeOffset At, string Text, string Engine);

/// <summary>Последние диктовки (history.bin, зашифровано DPAPI на Windows, ключом из Связки ключей на macOS), новые — первыми.</summary>
public sealed class HistoryStore(AppPaths paths)
{
    public const int MaxEntries = 20;

    private readonly object _gate = new();

    public IReadOnlyList<HistoryEntry> Load()
    {
        lock (_gate)
        {
            return LoadUnlocked();
        }
    }

    public IReadOnlyList<HistoryEntry> Add(HistoryEntry entry)
    {
        lock (_gate)
        {
            var list = LoadUnlocked();
            list.Insert(0, entry);
            if (list.Count > MaxEntries)
            {
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            }

            SaveUnlocked(list);
            return list;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            SaveUnlocked([]);
        }
    }

    private List<HistoryEntry> LoadUnlocked()
    {
        var cipher = StoreFile.TryReadAllBytes(paths.HistoryFile, "HistoryStore");
        if (cipher is null)
        {
            return [];
        }

        try
        {
            var plain = DataProtection.Unprotect(cipher);
            try
            {
                return JsonSerializer.Deserialize(plain, SaySoJsonContext.Default.ListHistoryEntry) ?? [];
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (DataProtectionUnavailableException e)
        {
            // Связка ключей не отдала ключ (отказ в доступе): файл цел, в карантин не переносим.
            Log.Error("HistoryStore", e);
            return [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Повреждённый файл: копия в history.bin.bad, история начинается заново.
            StoreFile.Quarantine(paths.HistoryFile, "HistoryStore", e);
            return [];
        }
    }

    private void SaveUnlocked(List<HistoryEntry> list)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(list, SaySoJsonContext.Default.ListHistoryEntry);
        try
        {
            AtomicFile.WriteAllBytes(paths.HistoryFile, DataProtection.Protect(json));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }
}
