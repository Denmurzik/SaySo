using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Diagnostics;
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
        if (!TrySave())
        {
            Info.Message = "Не удалось сохранить — файл занят, попробуйте ещё раз.";
            Info.Severity = InfoBarSeverity.Error;
            Info.IsOpen = true;
            return;
        }

        TermsBox.Text = string.Join("\r", _model.Settings.Vocabulary);
        _savedText = TermsBox.Text;
        Info.Message = $"Сохранено терминов: {_model.Settings.Vocabulary.Count}.";
        Info.Severity = InfoBarSeverity.Success;
        Info.IsOpen = true;
    }

    /// <summary>Уход со страницы или закрытие окна не должны терять несохранённые правки.</summary>
    private void OnUnloaded(object sender, RoutedEventArgs e) => SaveIfChanged();

    internal void SaveIfChanged()
    {
        if (TermsBox.Text != _savedText && TrySave())
        {
            _savedText = TermsBox.Text;
        }
    }

    private bool TrySave()
    {
        try
        {
            _model.Settings.Vocabulary = Parse(TermsBox.Text);
            _model.SaveSettings(); // нормализация: обрезка, дубликаты, лимит 1000
            return true;
        }
        catch (Exception e)
        {
            Log.Error("DictionaryPage.Save", e);
            return false;
        }
    }

    private void UpdateCounter()
    {
        var count = Parse(TermsBox.Text).Count;
        Counter.Text = count > SettingsNormalizer.MaxVocabularyTerms
            ? $"{count} — будет сохранено только {SettingsNormalizer.MaxVocabularyTerms}"
            : count > 100 ? $"{count} (рекомендуется до 100)" : $"{count}";
    }
}
