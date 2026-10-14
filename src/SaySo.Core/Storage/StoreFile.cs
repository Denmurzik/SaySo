using SaySo.Core.Diagnostics;

namespace SaySo.Core.Storage;

internal enum FileReadStatus
{
    Ok,
    Missing,

    /// <summary>Файл есть, но прочитать его не удалось (блокировка, права).</summary>
    Failed,
}

/// <summary>Общая политика чтения файлов данных: повтор при блокировке, карантин повреждённых файлов.</summary>
internal static class StoreFile
{
    private const int Attempts = 3;
    private const int RetryDelayMs = 50;

    /// <summary>
    /// Читает файл целиком. Нет файла — null без записи в журнал. Блокировка (IOException/UnauthorizedAccessException)
    /// повторяется до 3 раз с паузой 50 мс; если не прошло — запись в журнал и null (Load не должен бросать).
    /// </summary>
    public static byte[]? TryReadAllBytes(string path, string store)
    {
        Read(path, store, out var bytes);
        return bytes;
    }

    /// <summary>То же, но различает «файла нет» и «файл есть, а прочитать не удалось» (bytes == null в обоих случаях).</summary>
    public static FileReadStatus Read(string path, string store, out byte[]? bytes)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    bytes = null;
                    return FileReadStatus.Missing;
                }

                bytes = File.ReadAllBytes(path);
                return FileReadStatus.Ok;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                bytes = null;
                return FileReadStatus.Missing;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= Attempts)
                {
                    Log.Error(store, e);
                    bytes = null;
                    return FileReadStatus.Failed;
                }

                Thread.Sleep(RetryDelayMs);
            }
        }
    }

    /// <summary>Повреждённое содержимое: копия в «.bad» (перезапись) и запись в журнал без содержимого файла.</summary>
    public static void Quarantine(string path, string store, Exception e)
    {
        Log.Error(store, e);
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
        }
        catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
        {
            Log.Error(store + " (копия .bad)", copyError);
        }
    }
}
