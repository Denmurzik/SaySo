namespace SaySo.Core.Updates;

/// <summary>
/// Тексты обновлений вне плашки оверлея: всплывающее уведомление трея, пункт меню, страницы настроек.
/// Строки плашки (с проверкой ширины) — в <see cref="Overlay.OverlayMessages"/>.
/// </summary>
public static class UpdateMessages
{
    public const string BalloonTitle = "SaySo";

    /// <param name="manual">
    /// Поставить из приложения нельзя (администратор, неустановленная копия, нет Setup в релизе, а на macOS всегда): только
    /// страница релиза.
    /// </param>
    public static string Balloon(Version version, bool manual = false) => manual
        ? Available(version, manual: true)
        : $"Доступна версия {Format(version)}. Нажмите, чтобы обновить";

    public static string MenuItem(Version version, bool manual = false) =>
        manual ? $"Страница релиза {Format(version)}" : $"Обновить до {Format(version)}";

    public const string UpToDate = Overlay.OverlayMessages.UpdateUpToDate;

    public static string Available(Version version, bool manual = false) => manual
        ? $"Доступна версия {Format(version)}. Скачайте {(OperatingSystem.IsMacOS() ? "новую версию" : "установщик")} со страницы релиза"
        : $"Доступна версия {Format(version)}";

    /// <summary>Текст ошибки ручной проверки на странице «О программе».</summary>
    public static string CheckFailed(UpdateFailure failure) => failure switch
    {
        UpdateFailure.Network => "Не удалось проверить обновления: нет связи с GitHub",
        UpdateFailure.Timeout => "Не удалось проверить обновления: GitHub не ответил за 15 секунд",
        UpdateFailure.RateLimited => "Не удалось проверить обновления: GitHub ограничил число запросов, попробуйте позже",
        _ => "Не удалось проверить обновления: непонятный ответ GitHub",
    };

    private static string Format(Version version) => VersionTag.Normalize(version).ToString(3);
}
