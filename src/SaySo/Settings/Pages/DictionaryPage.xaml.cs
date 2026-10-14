using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Settings;

namespace SaySo.Settings.Pages;

public sealed partial class DictionaryPage : UserControl
{
    private readonly SettingsModel _model;

    internal DictionaryPage(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        TermsBox.Text = string.Join("\r", model.Settings.Vocabulary);
        UpdateCounter();
    }

    private static List<string> Parse(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void OnTermsChanged(object sender, TextChangedEventArgs e) => UpdateCounter();

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _model.Settings.Vocabulary = Parse(TermsBox.Text);
        _model.SaveSettings(); // нормализация: обрезка, дубликаты, лимит 1000
        TermsBox.Text = string.Join("\r", _model.Settings.Vocabulary);
        Info.Message = $"Сохранено терминов: {_model.Settings.Vocabulary.Count}.";
        Info.Severity = InfoBarSeverity.Success;
        Info.IsOpen = true;
    }

    private void UpdateCounter()
    {
        var count = Parse(TermsBox.Text).Count;
        Counter.Text = count > SettingsNormalizer.MaxVocabularyTerms
            ? $"{count} — будет сохранено только {SettingsNormalizer.MaxVocabularyTerms}"
            : count > 100 ? $"{count} (рекомендуется до 100)" : $"{count}";
    }
}
