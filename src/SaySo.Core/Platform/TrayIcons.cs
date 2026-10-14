using Microsoft.Win32;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

public enum TrayState
{
    Idle,
    Recording,
    Paused,
    Error,
}

/// <summary>
/// Набор значков трея под текущую тему панели задач (светлая/тёмная) и DPI системы.
/// Значок, который не удалось создать, равен 0 — <see cref="TrayIcon"/> с таким значком всё равно работает.
/// </summary>
internal sealed class TrayIcons : IDisposable
{
    private const char Microphone = '\uE720';
    private readonly Dictionary<TrayState, nint> _icons;

    private TrayIcons(Dictionary<TrayState, nint> icons)
    {
        _icons = icons;
    }

    public static TrayIcons Create()
    {
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, Native.GetDpiForSystem());
        if (size <= 0)
        {
            size = 16;
        }

        var fg = IsLightTaskbar() ? 0x1A1A1Au : 0xFFFFFFu;
        return new TrayIcons(new Dictionary<TrayState, nint>
        {
            [TrayState.Idle] = GlyphIcon.Create(Microphone, size, fg, null),
            [TrayState.Recording] = GlyphIcon.Create(Microphone, size, fg, 0xE81123),
            [TrayState.Paused] = GlyphIcon.Create(Microphone, size, 0x808080, null),
            [TrayState.Error] = GlyphIcon.Create(Microphone, size, fg, 0xF7A800),
        });
    }

    /// <returns>HICON состояния или 0, если значок не удалось создать.</returns>
    public nint Get(TrayState state) => _icons.GetValueOrDefault(state);

    public void Dispose()
    {
        foreach (var icon in _icons.Values)
        {
            if (icon != 0)
            {
                Native.DestroyIcon(icon);
            }
        }

        _icons.Clear();
    }

    private static bool IsLightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
    }
}
