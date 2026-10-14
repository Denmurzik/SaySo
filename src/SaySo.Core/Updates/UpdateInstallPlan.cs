namespace SaySo.Core.Updates;

public enum UpdateInstallAction
{
    /// <summary>Скачать и запустить Setup.</summary>
    Install,

    /// <summary>Не ставить, а открыть страницу релиза.</summary>
    OpenReleasePage,
}

/// <summary>Как обновлять эту копию SaySo (чистое решение по путям, без обращений к сети).</summary>
public sealed record UpdateInstallPlan(UpdateInstallAction Action, bool AllUsers)
{
    public const string UninstallerName = "unins000.exe";
    public const string SilentArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE";

    /// <summary>Для установки «для всех» Setup запускается через ShellExecute <c>runas</c> (UAC).</summary>
    public bool Elevate => Action == UpdateInstallAction.Install && AllUsers;

    /// <summary>Аргументы Setup; для <see cref="UpdateInstallAction.OpenReleasePage"/> — пусто.</summary>
    public string Arguments => Action == UpdateInstallAction.Install
        ? SilentArguments + (AllUsers ? " /ALLUSERS" : " /CURRENTUSER")
        : "";

    /// <param name="exePath">Путь к запущенному SaySo.exe.</param>
    /// <param name="programFilesRoots">Каталоги Program Files (64- и 32-разрядный).</param>
    /// <param name="fileExists">Проверка наличия файла (подменяется в тестах).</param>
    /// <param name="releaseInstallable">В релизе есть Setup.exe и .sha256.</param>
    public static UpdateInstallPlan Decide(string exePath, IEnumerable<string> programFilesRoots, Func<string, bool> fileExists, bool releaseInstallable)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(exePath));
        // SaySo не из установки (рядом нет деинсталлятора Inno: портативная копия, install.ps1, сборка из исходников) или в релизе
        // нет установщика — поверх такую копию не ставим.
        if (!releaseInstallable || directory is null || !fileExists(Path.Combine(directory, UninstallerName)))
        {
            return new UpdateInstallPlan(UpdateInstallAction.OpenReleasePage, false);
        }

        return new UpdateInstallPlan(UpdateInstallAction.Install, programFilesRoots.Any(root => IsUnder(directory, root)));
    }

    public static IReadOnlyList<string> SystemProgramFilesRoots() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        }.Where(p => !string.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    internal static bool IsUnder(string directory, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var separators = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        var dir = Path.GetFullPath(directory).TrimEnd(separators);
        var top = Path.GetFullPath(root).TrimEnd(separators);
        return dir.Equals(top, StringComparison.OrdinalIgnoreCase)
               || dir.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
