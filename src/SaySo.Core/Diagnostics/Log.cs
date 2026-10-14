namespace SaySo.Core.Diagnostics;

/// <summary>
/// Минимальный файловый лог для диагностики (%LOCALAPPDATA%\SaySo\sayso.log, ротация по 256 КБ).
/// Никогда не пишите сюда тексты диктовок и ключи API.
/// </summary>
public static class Log
{
    public const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    public static string? FilePath { get; set; }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string context, Exception e) => Write("ERROR", $"{context}: {e.GetType().Name}: {e.Message}");

    private static void Write(string level, string message)
    {
        var path = FilePath;
        if (path is null)
        {
            return;
        }

        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Лог — по возможности; диктовка важнее.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
