namespace SaySo.Core.Audio;

/// <summary>
/// Находит звук темы прямо в образе сборки: встроенный ресурс отдаётся потоком UnmanagedMemoryStream, а его PositionPointer
/// указывает в сам образ SaySo.exe. Ничего не копируется, не закрепляется и не освобождается: образ не выгружается,
/// указатель действителен всё время работы процесса (в том числе пока PlaySound с SND_ASYNC читает память после возврата).
/// </summary>
public sealed class SoundThemeLoader(Func<string, Stream?> openResource)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, nint> _pointers = [];

    public SoundThemeLoader()
        : this(OpenEmbedded)
    {
    }

    /// <summary>Указатель на начало WAV (заголовок RIFF) звука этого вида в теме.</summary>
    /// <exception cref="InvalidOperationException">Ресурса нет или он не отдаётся указателем на память.</exception>
    public nint Get(string themeId, SoundKind kind)
    {
        var name = SoundThemes.ResourceName(themeId, kind);
        lock (_gate)
        {
            if (!_pointers.TryGetValue(name, out var pointer))
            {
                pointer = Locate(name);
                _pointers[name] = pointer;
            }

            return pointer;
        }
    }

    private nint Locate(string name)
    {
        using var stream = openResource(name) ?? throw new InvalidOperationException($"В сборке нет ресурса {name}.");
        if (stream is not UnmanagedMemoryStream { Position: 0 } memory)
        {
            throw new InvalidOperationException($"Ресурс {name} не отдаётся указателем на память образа.");
        }

        try
        {
            return PositionOf(memory); // указатель берётся до Dispose: сама память потоку не принадлежит
        }
        catch (NotSupportedException e)
        {
            throw new InvalidOperationException($"У потока ресурса {name} нет прямого указателя на память.", e);
        }
    }

    private static unsafe nint PositionOf(UnmanagedMemoryStream stream) => (nint)stream.PositionPointer;

    private static Stream? OpenEmbedded(string name) => typeof(SoundThemeLoader).Assembly.GetManifestResourceStream(name);
}
