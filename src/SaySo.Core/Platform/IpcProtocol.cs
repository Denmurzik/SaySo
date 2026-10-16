namespace SaySo.Core.Platform;

/// <summary>
/// Протокол «окно настроек → демон». Windows: зарегистрированные оконные сообщения скрытому окну демона (окно верхнего
/// уровня, не message-only, поэтому ищется через FindWindowEx(0, …)). macOS: строка сообщения в Unix-сокет демона.
/// </summary>
public static class IpcProtocol
{
    public const string WindowClass = "SaySo.Daemon.Ipc";
    public const string ReloadSettings = "SaySo.ReloadSettings";
    public const string SuspendHotkey = "SaySo.SuspendHotkey";
    public const string ResumeHotkey = "SaySo.ResumeHotkey";
    public const string OpenSettings = "SaySo.OpenSettings";
    public const string Quit = "SaySo.Quit";

    /// <summary>Окно настроек нашло новую версию: демон проверяет сам (без суточного ограничения, без уведомления).</summary>
    public const string CheckUpdates = "SaySo.CheckUpdates";

    /// <summary>
    /// Установить обновление: демон ставит известную ему версию (из последней проверки); если не знает ни одной — сначала
    /// проверяет. Дальше скачивает, проверяет SHA-256 и запускает Setup; повышенный демон только открывает страницу релиза.
    /// </summary>
    public const string InstallUpdate = "SaySo.InstallUpdate";

    /// <summary>Сколько демон ждёт завершения диктовки при выходе (потом выходит принудительно).</summary>
    public static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(10);

    public static readonly string[] AllMessages = [ReloadSettings, SuspendHotkey, ResumeHotkey, OpenSettings, Quit, CheckUpdates, InstallUpdate];
}
