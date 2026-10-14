namespace SaySo.Core.Platform;

/// <summary>Полные пути к системным программам: поиск по имени в PATH и текущем каталоге подсунул бы чужой файл.</summary>
public static class ShellPaths
{
    /// <summary>
    /// <c>%WINDIR%\explorer.exe</c>. Запуск через Проводник нужен, чтобы программа стартовала без повышения прав,
    /// даже если вызывающий процесс запущен от администратора.
    /// </summary>
    public static string Explorer { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
}
