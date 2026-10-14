using SaySo.Core.Audio;
using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

public enum SoundKind
{
    Start,
    Stop,
    Error,
}

/// <summary>
/// Короткие сигналы выбранной звуковой темы. PlaySound(SND_MEMORY|SND_ASYNC) читает буфер после возврата, поэтому буфер
/// закреплён (POH) и остаётся у загрузчика, пока тема не сменится; перед сменой звук останавливается.
/// </summary>
public static unsafe class SoundPlayer
{
    private static readonly object Gate = new();
    private static readonly SoundThemeLoader Loader = new();
    private static bool _played; // до первого воспроизведения winmm.dll в процесс не загружается (в том числе при смене темы)

    /// <summary>Выбрать тему заранее (например, после перечитывания настроек): буферы прежней темы отпускаются сразу.</summary>
    public static void Select(string? themeId)
    {
        lock (Gate)
        {
            SelectLocked(SoundThemes.Normalize(themeId));
        }
    }

    public static void Play(SoundKind kind, string? themeId)
    {
        lock (Gate)
        {
            try
            {
                SelectLocked(SoundThemes.Normalize(themeId));
                var data = Loader.Get(kind);
                fixed (byte* p = data)
                {
                    Native.PlaySoundW(p, 0, Native.SND_ASYNC | Native.SND_MEMORY | Native.SND_NODEFAULT);
                }

                _played = true;
            }
            catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException)
            {
                Log.Error("Воспроизведение звука", e); // сигнал не обязателен: диктовка из-за него не должна падать
            }
        }
    }

    private static void SelectLocked(string themeId)
    {
        if (string.Equals(Loader.ThemeId, themeId, StringComparison.Ordinal))
        {
            return;
        }

        // Звук прежней темы мог ещё играть из её буфера: останавливаем до того, как буфер отпустится.
        if (_played)
        {
            Native.PlaySoundW(null, 0, 0);
            _played = false;
        }

        Loader.Select(themeId);
        Log.Info($"Звуковая тема: {themeId}");
    }
}
