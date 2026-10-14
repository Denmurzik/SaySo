using SaySo.Core.Text;

namespace SaySo.Core.Input;

/// <summary>Последняя вставка: окно, время и вставленный текст.</summary>
public sealed record LastInsertion(nint Window, DateTimeOffset At, string Text);

/// <summary>Пробел между подряд идущими диктовками в одно окно (иначе предложения слипаются).</summary>
public static class SpaceJoiner
{
    public static string Prepare(string text, LastInsertion? last, nint target, DateTimeOffset now) =>
        last is not null
        && last.Window == target
        && now - last.At < TextInserter.JoinWindow
        && TextJoin.NeedsSpaceBetween(last.Text, text)
            ? " " + text
            : text;
}
