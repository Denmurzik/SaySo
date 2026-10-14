using SaySo.Core.Audio;
using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;

namespace SaySo.Core.Platform;

/// <summary>
/// Короткие сигналы выбранной звуковой темы. PlaySound(SND_MEMORY|SND_ASYNC) читает буфер после возврата, поэтому играем
/// прямо из встроенного ресурса: он лежит в образе SaySo.exe, который живёт, пока жив процесс (копий и закрепления нет).
/// </summary>
public static unsafe class SoundPlayer
{
    private static readonly SoundThemeLoader Loader = new();
    private static string? _loggedTheme;

    public static void Play(SoundKind kind, string? themeId)
    {
        try
        {
            var theme = SoundThemes.Normalize(themeId);
            if (!Native.PlaySoundW((void*)Loader.Get(theme, kind), 0, Native.SND_ASYNC | Native.SND_MEMORY | Native.SND_NODEFAULT))
            {
                Log.Info($"Звук не воспроизведён: тема {theme}, {kind}"); // нет устройства вывода или система отказала; не исключение
                return;
            }

            // Строка о теме пишется только после успешного воспроизведения: по журналу видно, что звук действительно пошёл.
            if (!string.Equals(Interlocked.Exchange(ref _loggedTheme, theme), theme, StringComparison.Ordinal))
            {
                Log.Info($"Звуковая тема: {theme}");
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Log.Error("Воспроизведение звука", e); // сигнал не обязателен: диктовка из-за него не должна падать
        }
    }
}
