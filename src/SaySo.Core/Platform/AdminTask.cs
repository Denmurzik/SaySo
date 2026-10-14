using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;

namespace SaySo.Core.Platform;

/// <summary>
/// Автозапуск с правами администратора — задача Планировщика «при входе» с наивысшими правами.
/// Создаётся из XML, потому что у schtasks /Create по умолчанию «не запускать от батареи» и лимит 3 дня.
/// </summary>
public static class AdminTask
{
    public const string TaskName = "SaySo";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    public static string BuildXml(string exePath, string userId)
    {
        XElement E(string name, object? content = null) => new(Ns + name, content);
        var task = new XElement(Ns + "Task",
            new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", "SaySo — голосовая диктовка (запуск с правами администратора)")),
            E("Triggers", E("LogonTrigger", new object[] { E("Enabled", "true"), E("UserId", userId) })),
            E("Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                E("UserId", userId), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new object[]
            {
                E("MultipleInstancesPolicy", "IgnoreNew"),
                E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"),
                E("AllowHardTerminate", "true"),
                E("StartWhenAvailable", "false"),
                E("RunOnlyIfNetworkAvailable", "false"),
                E("IdleSettings", new object[] { E("StopOnIdleEnd", "false"), E("RestartOnIdle", "false") }),
                E("AllowStartOnDemand", "true"),
                E("Enabled", "true"),
                E("Hidden", "false"),
                E("RunOnlyIfIdle", "false"),
                E("ExecutionTimeLimit", "PT0S"),
                E("Priority", "4"),
            }),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), E("Exec", E("Command", exePath))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
    }

    public static bool Exists() => Schtasks("/Query", "/TN", TaskName) == 0;

    /// <summary>Требует прав администратора (вызывается из `SaySo.exe --install-admin-task` через UAC).</summary>
    public static int Install(string exePath)
    {
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var file = Path.Combine(Path.GetTempPath(), $"sayso-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, BuildXml(exePath, user), Encoding.Unicode);
        try
        {
            return Schtasks("/Create", "/TN", TaskName, "/XML", file, "/F");
        }
        finally
        {
            File.Delete(file);
        }
    }

    public static int Remove() => Schtasks("/Delete", "/TN", TaskName, "/F");

    public static int Run() => Schtasks("/Run", "/TN", TaskName);

    /// <returns>Код завершения schtasks или -1, если запустить его не удалось / он завис.</returns>
    private static int Schtasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return -1;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15_000))
            {
                process.Kill();
                return -1;
            }

            return process.ExitCode;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return -1;
        }
    }
}
