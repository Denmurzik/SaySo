using Microsoft.Win32;

namespace SaySo.Core.Platform;

/// <summary>Автозапуск через HKCU\…\Run (обычный режим, без прав администратора).</summary>
public sealed class AutostartManager(
    string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run",
    string valueName = "SaySo")
{
    public bool IsEnabled(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath);
        return key?.GetValue(valueName) is string value && string.Equals(value, Quote(exePath), StringComparison.OrdinalIgnoreCase);
    }

    public void Set(bool enabled, string exePath)
    {
        if (enabled == IsEnabled(exePath))
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true);
        if (enabled)
        {
            key.SetValue(valueName, Quote(exePath));
        }
        else
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
    }

    private static string Quote(string path) => "\"" + path + "\"";
}
