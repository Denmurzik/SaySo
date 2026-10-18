using SaySo.Core.Settings;

namespace SaySo.Mac.Settings.Pages;

/// <summary>«Словарь»: термины по одному на строку; запись по кнопке и при уходе со страницы.</summary>
internal sealed class DictionaryPage : IPage
{
    private readonly SettingsModel _model;
    private readonly Ui _ui = new();
    private readonly nint _info;
    private readonly nint _text;
    private readonly nint _counter;
    private string _savedText;

    public DictionaryPage(SettingsModel model)
    {
        _model = model;
        var (scroll, column) = Ui.ScrollPage();
        View = scroll;
        _info = Ui.InfoLine();
        var (area, text) = _ui.TextArea(string.Join("\n", model.Settings.Vocabulary), 320, UpdateCounter);
        _text = text;
        _savedText = Ui.TextOf(_text);
        _counter = Ui.Label("", 12, secondary: true);
        foreach (var view in new[]
        {
            Ui.Title("Словарь"),
            Ui.Label("Имена, названия и термины, которые нужно распознавать точно (например, Kubernetes, SaySo, Иван Петров). По одному на строку. Лучше всего работает до 100 терминов, максимум 1000.",
                12, secondary: true, wrapWidth: Ui.PageWidth),
            _info,
            area,
            Ui.Row(16, _ui.AccentButton("Сохранить", OnSave), _counter),
        })
        {
            Ui.Append(column, view);
        }

        UpdateCounter();
    }

    public string Title => "Словарь";

    public string Symbol => "character.book.closed";

    public nint View { get; }

    /// <summary>Уход со страницы или закрытие окна не должны терять несохранённые правки.</summary>
    public void OnLeave() => SaveIfChanged();

    public void Dispose() => _ui.Dispose();

    private static List<string> Parse(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void OnSave()
    {
        var saved = Commit();
        Ui.SetTextOf(_text, string.Join("\n", _model.Settings.Vocabulary));
        _savedText = Ui.TextOf(_text);
        UpdateCounter();
        Ui.Inform(_info, saved ? $"Сохранено терминов: {_model.Settings.Vocabulary.Count}." : SettingsModel.SaveFailedMessage,
            saved ? Severity.Success : Severity.Error);
    }

    /// <summary>Если файл занят, значение остаётся в модели «несохранённым»: его повторно запишет Flush окна.</summary>
    private void SaveIfChanged()
    {
        var text = Ui.TextOf(_text);
        if (text == _savedText)
        {
            return;
        }

        _savedText = text;
        Commit();
    }

    private bool Commit()
    {
        _model.Settings.Vocabulary = Parse(Ui.TextOf(_text));
        return _model.SaveSettings(); // нормализация: обрезка, дубликаты, лимит 1000
    }

    private void UpdateCounter()
    {
        var count = Parse(Ui.TextOf(_text)).Count;
        Ui.SetText(_counter, count > SettingsNormalizer.MaxVocabularyTerms
            ? $"{count} (будет сохранено только {SettingsNormalizer.MaxVocabularyTerms})"
            : count > 100 ? $"{count} (рекомендуется до 100)" : $"Терминов: {count}");
    }
}
