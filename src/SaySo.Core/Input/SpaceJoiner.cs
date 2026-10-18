using SaySo.Core.Text;

namespace SaySo.Core.Input;

/// <summary>Последняя вставка: окно (Windows: HWND, macOS: идентификатор процесса приложения), время и вставленный текст.</summary>
public sealed record LastInsertion(nint Window, DateTimeOffset At, string Text);

/// <summary>Пробел между подряд идущими диктовками в одно окно (иначе предложения слипаются).</summary>
public static class SpaceJoiner
{
    /// <summary>Через сколько после прошлой вставки в то же окно новая диктовка уже не продолжает её.</summary>
    public static readonly TimeSpan JoinWindow = TimeSpan.FromSeconds(60);

    public static string Prepare(string text, LastInsertion? last, nint target, DateTimeOffset now) =>
        last is not null
        && last.Window == target
        && now - last.At < JoinWindow
        && TextJoin.NeedsSpaceBetween(last.Text, text)
            ? " " + text
            : text;
}
