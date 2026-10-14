namespace SaySo.Core.Text;

/// <summary>Склейка фрагментов текста с правильным пробелом между ними.</summary>
public static class TextJoin
{
    private const string NoSpaceBefore = ".,!?;:…)]}»”%";
    private const string NoSpaceAfter = "([{«“";

    public static bool NeedsSpaceBetween(string left, string right) =>
        left.Length > 0
        && right.Length > 0
        && !char.IsWhiteSpace(left[^1])
        && !char.IsWhiteSpace(right[0])
        && !NoSpaceBefore.Contains(right[0])
        && !NoSpaceAfter.Contains(left[^1]);

    public static string Join(string left, string right) =>
        NeedsSpaceBetween(left, right) ? left + " " + right : left + right;
}
