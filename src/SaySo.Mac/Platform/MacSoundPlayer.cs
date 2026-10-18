using System.Buffers.Binary;
using SaySo.Core.Audio;
using SaySo.Core.Diagnostics;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>
/// Сигналы звуковой темы через NSSound. WAV не копируется: NSData ссылается прямо на встроенный ресурс в образе программы
/// (<see cref="SoundThemeLoader"/>), как PlaySound(SND_MEMORY) в Windows. Звуки выбранной темы создаются при первом
/// воспроизведении и живут, пока тема не сменится. Только главный поток.
/// </summary>
internal static unsafe class MacSoundPlayer
{
    private static readonly SoundThemeLoader Loader = new();
    private static readonly Dictionary<SoundKind, nint> Sounds = [];
    private static string? _theme;

    public static void Play(SoundKind kind, string? themeId)
    {
        try
        {
            var theme = SoundThemes.Normalize(themeId);
            if (!string.Equals(theme, _theme, StringComparison.Ordinal))
            {
                ReleaseAll();
                _theme = theme;
                Log.Info($"Звуковая тема: {theme}");
            }

            if (!Sounds.TryGetValue(kind, out var sound))
            {
                sound = Create(theme, kind);
                Sounds[kind] = sound;
            }

            ObjC.SendBool(sound, ObjC.Sel("stop")); // повтор того же сигнала, пока прежний ещё звучит
            if (!ObjC.SendBool(sound, ObjC.Sel("play")))
            {
                Log.Info($"Звук не воспроизведён: тема {theme}, {kind}");
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            Log.Error("Воспроизведение звука", e); // сигнал не обязателен: диктовка из-за него не должна падать
        }
    }

    private static nint Create(string theme, SoundKind kind)
    {
        var wav = (byte*)Loader.Get(theme, kind);
        // RIFF: «RIFF», размер (без первых 8 байт), «WAVE»…
        var length = (nint)BinaryPrimitives.ReadUInt32LittleEndian(new ReadOnlySpan<byte>(wav + 4, 4)) + 8;
        var data = ObjC.Send(ObjC.Class("NSData"), ObjC.Sel("dataWithBytesNoCopy:length:freeWhenDone:"), (nint)wav, length, false);
        var sound = ObjC.Send(ObjC.Alloc("NSSound"), ObjC.Sel("initWithData:"), data);
        if (sound == 0)
        {
            throw new InvalidOperationException($"NSSound не принял звук {theme}/{kind}.");
        }

        return sound; // +1, освобождается при смене темы
    }

    private static void ReleaseAll()
    {
        foreach (var sound in Sounds.Values)
        {
            ObjC.Release(sound);
        }

        Sounds.Clear();
    }
}
