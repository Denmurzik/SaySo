using SaySo.Core.Text;

namespace SaySo.Core.Engines.Gemini;

/// <summary>
/// Собирает итоговый текст из финальных сегментов. Поддерживает и приращения («Привет.» + «Как дела?»),
/// и накопительные финалы («Привет.» → «Привет. Как дела?»): накопительным считается только финал,
/// строго длиннее накопленного и начинающийся с него; равный или короче — новый сегмент. Не потокобезопасен.
/// </summary>
public sealed class TranscriptAssembler
{
    private string _final = "";

    public string? PendingInterim { get; private set; }

    public int FinalCount { get; private set; }

    public void AddFinal(string text)
    {
        PendingInterim = null;
        text = text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        _final = _final.Length > 0 && text.Length > _final.Length && text.StartsWith(_final, StringComparison.Ordinal)
            ? text
            : TextJoin.Join(_final, text);
        FinalCount++;
    }

    public void SetInterim(string text) =>
        PendingInterim = string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public string BuildResult(bool includePendingInterim) =>
        includePendingInterim && PendingInterim is { } interim ? TextJoin.Join(_final, interim) : _final;
}
