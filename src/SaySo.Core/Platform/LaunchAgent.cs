using System.Security;
using System.Text;

namespace SaySo.Core.Platform;

/// <summary>
/// macOS: автозапуск при входе — LaunchAgent пользователя (~/Library/LaunchAgents/&lt;Label&gt;.plist), аналог ключа Run
/// в Windows. Пакет SaySo.app запускается через <c>/usr/bin/open -g -a</c>: так его запускает LaunchServices (значка в
/// Dock нет, разрешения macOS выдаются самому SaySo, а не launchd), без перевода на передний план. Копия вне пакета
/// (сборка для разработки) запускается напрямую. Включение и выключение вступают в силу со следующего входа.
/// </summary>
public sealed class LaunchAgent(string directory)
{
    public const string Label = "io.github.denmurzik.sayso";

    public LaunchAgent()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents"))
    {
    }

    public string PlistPath => Path.Combine(directory, Label + ".plist");

    /// <summary>Автозапуск включён именно для этой копии SaySo.</summary>
    public bool IsEnabled(string exePath)
    {
        try
        {
            return File.Exists(PlistPath) && File.ReadAllText(PlistPath) == Build(exePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Set(bool enabled, string exePath)
    {
        if (enabled == IsEnabled(exePath))
        {
            return;
        }

        if (enabled)
        {
            Directory.CreateDirectory(directory);
            var temp = PlistPath + ".tmp";
            File.WriteAllText(temp, Build(exePath), new UTF8Encoding(false));
            File.Move(temp, PlistPath, overwrite: true);
        }
        else
        {
            File.Delete(PlistPath);
        }
    }

    /// <summary>Пакет .app, в котором лежит исполняемый файл (…/SaySo.app/Contents/MacOS/SaySo); null — файл не в пакете.</summary>
    public static string? AppBundleOf(string exePath)
    {
        var macOs = Path.GetDirectoryName(exePath);
        var contents = macOs is null ? null : Path.GetDirectoryName(macOs);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return bundle is not null
               && string.Equals(Path.GetFileName(macOs), "MacOS", StringComparison.Ordinal)
               && string.Equals(Path.GetFileName(contents), "Contents", StringComparison.Ordinal)
               && bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle
            : null;
    }

    public static string Build(string exePath)
    {
        var bundle = AppBundleOf(exePath);
        string[] arguments = bundle is not null ? ["/usr/bin/open", "-g", "-a", bundle] : [exePath];
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n<dict>\n");
        sb.Append("\t<key>Label</key>\n\t<string>").Append(Label).Append("</string>\n");
        sb.Append("\t<key>ProgramArguments</key>\n\t<array>\n");
        foreach (var argument in arguments)
        {
            sb.Append("\t\t<string>").Append(SecurityElement.Escape(argument)).Append("</string>\n");
        }

        sb.Append("\t</array>\n");
        sb.Append("\t<key>RunAtLoad</key>\n\t<true/>\n");
        sb.Append("\t<key>LimitLoadToSessionType</key>\n\t<string>Aqua</string>\n");
        sb.Append("\t<key>ProcessType</key>\n\t<string>Interactive</string>\n");
        sb.Append("</dict>\n</plist>\n");
        return sb.ToString();
    }
}
