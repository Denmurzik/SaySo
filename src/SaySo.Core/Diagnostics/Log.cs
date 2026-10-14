using System.Text;

namespace SaySo.Core.Diagnostics;

/// <summary>
/// Минимальный файловый лог для диагностики (%LOCALAPPDATA%\SaySo\sayso.log, ротация по 256 КБ).
/// Никогда не пишите сюда тексты диктовок и ключи API.
/// </summary>
public static class Log
{
    public const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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

                // Журнал пишут демон и окно настроек одновременно: общий доступ на запись, иначе вторая запись падает с IOException
                // и строка пропадает; FileShare.Delete — чтобы ротация (переименование) одного процесса не ломала другого.
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {level} {message}{Environment.NewLine}";
                stream.Write(Utf8NoBom.GetBytes(line));
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
