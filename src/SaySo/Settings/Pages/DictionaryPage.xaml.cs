using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Settings;

namespace SaySo.Settings.Pages;

public sealed partial class DictionaryPage : UserControl
{
    private readonly SettingsModel _model;
    private string _savedText;

    internal DictionaryPage(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        TermsBox.Text = string.Join("\r", model.Settings.Vocabulary);
        _savedText = TermsBox.Text;
        UpdateCounter();
    }

    private static List<string> Parse(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void OnTermsChanged(object sender, TextChangedEventArgs e) => UpdateCounter();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var saved = Commit();
        TermsBox.Text = string.Join("\r", _model.Settings.Vocabulary);
        _savedText = TermsBox.Text;
        if (saved)
        {
            Info.Message = $"Сохранено терминов: {_model.Settings.Vocabulary.Count}.";
            Info.Severity = InfoBarSeverity.Success;
        }
        else
        {
            Info.Message = SettingsModel.SaveFailedMessage;
            Info.Severity = InfoBarSeverity.Error;
        }

        Info.IsOpen = true;
    }

    /// <summary>Уход со страницы или закрытие окна не должны терять несохранённые правки.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) => SaveIfChanged();

    /// <summary>
    /// Передать правки в модель и записать. Если файл занят, значение остаётся в модели «несохранённым»
    /// (его повторно запишет Flush при переходе/закрытии и покажет предупреждение), поэтому повторно не пытаемся.
    /// </summary>
    internal void SaveIfChanged()
    {
        if (TermsBox.Text == _savedText)
        {
            return;
        }

        _savedText = TermsBox.Text;
        Commit();
    }

    private bool Commit()
    {
        _model.Settings.Vocabulary = Parse(TermsBox.Text);
        return _model.SaveSettings(); // нормализация: обрезка, дубликаты, лимит 1000
    }

    private void UpdateCounter()
    {
        var count = Parse(TermsBox.Text).Count;
        Counter.Text = count > SettingsNormalizer.MaxVocabularyTerms
            ? $"{count} — будет сохранено только {SettingsNormalizer.MaxVocabularyTerms}"
            : count > 100 ? $"{count} (рекомендуется до 100)" : $"{count}";
    }
}
