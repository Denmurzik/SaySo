using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using SaySo.Core.Diagnostics;

namespace SaySo.Core.Updates;

public enum UpdateInstallAction
{
    /// <summary>Скачать и запустить Setup.</summary>
    Install,

    /// <summary>Не ставить, а открыть страницу релиза.</summary>
    OpenReleasePage,
}

/// <summary>Куда Inno Setup записал ключ удаления: HKLM — «для всех пользователей», HKCU — «только для меня».</summary>
public enum InstallHive
{
    Machine,
    User,
}

/// <param name="AppPath">Каталог установки из ключа («Inno Setup: App Path» или InstallLocation); null — ключ есть, а пути в нём нет.</param>
public sealed record InstallEntry(string? AppPath);

/// <summary>Чтение ключа удаления Inno Setup этого приложения (<c>AppId</c> из SaySo.iss, суффикс <c>_is1</c>).</summary>
[SupportedOSPlatform("windows")]
public static class InstallRegistry
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{4C827BFC-9A72-4512-910C-1DDC1336642F}_is1";

    /// <returns>null — ключа нет (SaySo не ставился установщиком в этот режим) или он недоступен.</returns>
    public static InstallEntry? Read(InstallHive hive)
    {
        try
        {
            using var key = (hive == InstallHive.Machine ? Registry.LocalMachine : Registry.CurrentUser).OpenSubKey(UninstallKey);
            if (key is null)
            {
                return null;
            }

            var path = key.GetValue("Inno Setup: App Path") as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                path = key.GetValue("InstallLocation") as string;
            }

            return new InstallEntry(string.IsNullOrWhiteSpace(path) ? null : path.Trim().Trim('"'));
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            Log.Error("Чтение ключа удаления", e);
            return null;
        }
    }
}

/// <summary>Как обновлять эту копию SaySo (чистое решение по путям и ключам удаления, без обращений к сети).</summary>
public sealed record UpdateInstallPlan(UpdateInstallAction Action, bool AllUsers)
{
    public const string SilentArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE";

    /// <summary>
    /// Аргументы Setup; для <see cref="UpdateInstallAction.OpenReleasePage"/> — пусто. Setup всегда запускается без повышения
    /// прав: для <c>/ALLUSERS</c> UAC запрашивает он сам (<c>PrivilegesRequired=lowest</c>).
    /// </summary>
    public string Arguments => Action == UpdateInstallAction.Install
        ? SilentArguments + (AllUsers ? " /ALLUSERS" : " /CURRENTUSER")
        : "";

    private static UpdateInstallPlan Manual { get; } = new(UpdateInstallAction.OpenReleasePage, false);

    /// <param name="exePath">Путь к запущенному SaySo.exe.</param>
    /// <param name="programFilesRoots">Каталоги Program Files (64- и 32-разрядный) — запасное правило.</param>
    /// <param name="readEntry">Чтение ключа удаления Inno (подменяется в тестах).</param>
    /// <param name="releaseInstallable">В релизе есть Setup.exe и .sha256.</param>
    /// <param name="elevated">
    /// Процесс запущен с правами администратора: ничего не скачиваем и не запускаем (скачивание в доступный пользователю
    /// %TEMP% и запуск Setup с унаследованным повышенным токеном — путь к повышению прав), только страница релиза.
    /// </param>
    public static UpdateInstallPlan Decide(
        string exePath,
        IEnumerable<string> programFilesRoots,
        Func<InstallHive, InstallEntry?> readEntry,
        bool releaseInstallable,
        bool elevated = false)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(exePath));
        if (elevated || !releaseInstallable || directory is null)
        {
            return Manual;
        }

        // Режим берётся из ключа удаления, чей каталог — каталог запущенного exe; нет ключа — это не установленная копия
        // (портативная, install.ps1, сборка из исходников), поверх неё не ставим.
        var machine = readEntry(InstallHive.Machine);
        var user = readEntry(InstallHive.User);
        var machineMatches = machine?.AppPath is { } m && SamePath(directory, m);
        var userMatches = user?.AppPath is { } u && SamePath(directory, u);
        if (machineMatches != userMatches)
        {
            return new UpdateInstallPlan(UpdateInstallAction.Install, machineMatches);
        }

        // Оба ключа указывают сюда или в ключе нет пути: правило по Program Files. Ключ с другим каталогом — это не наша копия.
        var pathUnknown = machine is { AppPath: null } || user is { AppPath: null };
        if (machineMatches || pathUnknown)
        {
            return new UpdateInstallPlan(UpdateInstallAction.Install, programFilesRoots.Any(root => IsUnder(directory, root)));
        }

        return Manual;
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

        var dir = Normalize(directory);
        var top = Normalize(root);
        return dir.Equals(top, StringComparison.OrdinalIgnoreCase)
               || dir.StartsWith(top + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool SamePath(string a, string b)
    {
        try
        {
            return Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false; // путь из реестра с недопустимыми символами — не наш
        }
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
