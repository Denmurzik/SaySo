using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Dictation;
using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Engines.Groq;
using SaySo.Core.Settings;

namespace SaySo.Settings.Pages;

public sealed partial class RecognitionPage : UserControl
{
    private static readonly (string Label, string[] Codes)[] Languages =
    [
        ("Автоопределение", []),
        ("Русский (ru-RU)", ["ru-RU"]),
        ("English (en-US)", ["en-US"]),
    ];

    private readonly SettingsModel _model;
    private bool _loading = true;

    internal RecognitionPage(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        var r = model.Settings.Recognition;
        foreach (var key in model.Secrets.GeminiApiKeys)
        {
            AddGeminiKeyRow(key);
        }

        if (model.Secrets.GeminiApiKeys.Count == 0)
        {
            AddGeminiKeyRow("");
        }

        GroqKeyBox.Password = model.Secrets.GroqApiKey ?? "";
        ModeButtons.SelectedIndex = r.Mode == TranscriptionMode.Smart ? 0 : 1;
        GeminiModelBox.Text = r.GeminiModel;
        FallbackSwitch.IsOn = r.GroqFallbackEnabled;
        CleanupSwitch.IsOn = r.GroqCleanupEnabled;
        WhisperModelBox.Text = r.GroqWhisperModel;
        CleanupModelBox.Text = r.GroqCleanupModel;

        foreach (var (label, _) in Languages)
        {
            LanguageBox.Items.Add(label);
        }

        var index = Array.FindIndex(Languages, l => l.Codes.SequenceEqual(r.LanguageCodes, StringComparer.OrdinalIgnoreCase));
        if (index < 0)
        {
            LanguageBox.Items.Add("Свои: " + string.Join(", ", r.LanguageCodes));
            index = LanguageBox.Items.Count - 1;
        }

        LanguageBox.SelectedIndex = index;
        _loading = false;
    }

    private void OnAddGeminiKey(object sender, RoutedEventArgs e) =>
        AddGeminiKeyRow("").Focus(FocusState.Programmatic);

    // Строка списка ключей: поле ключа + «Удалить». Сохраняется при потере фокуса и при удалении.
    private PasswordBox AddGeminiKeyRow(string key)
    {
        var box = new PasswordBox { Password = key, PasswordRevealMode = PasswordRevealMode.Peek, PlaceholderText = "AIza…" };
        box.LostFocus += (_, _) => CommitGeminiKeys();
        var remove = new Button { Content = "Удалить", VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);
        remove.Click += (_, _) =>
        {
            GeminiKeysPanel.Children.Remove(row);
            CommitGeminiKeys();
        };
        GeminiKeysPanel.Children.Add(row);
        return box;
    }

    private void CommitGeminiKeys()
    {
        if (_loading)
        {
            return;
        }

        _model.SetGeminiKeys(GeminiKeysPanel.Children.OfType<Grid>()
            .Select(row => row.Children.OfType<PasswordBox>().First().Password));
        _model.SaveSecrets();
    }

    private void OnGroqKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _model.SetGroqKey(GroqKeyBox.Password);
        }
    }

    private void OnKeyLostFocus(object sender, RoutedEventArgs e) => _model.SaveSecrets();

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _model.Settings.Recognition.Mode = ModeButtons.SelectedIndex == 1 ? TranscriptionMode.Verbatim : TranscriptionMode.Smart;
        _model.SaveSettings();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0 || LanguageBox.SelectedIndex >= Languages.Length)
        {
            return; // пункт «Свои: …» оставляет коды как есть
        }

        _model.Settings.Recognition.LanguageCodes = Languages[LanguageBox.SelectedIndex].Codes.ToList();
        _model.SaveSettings();
    }

    private void OnModelLostFocus(object sender, RoutedEventArgs e)
    {
        var r = _model.Settings.Recognition;
        r.GeminiModel = GeminiModelBox.Text;
        r.GroqWhisperModel = WhisperModelBox.Text;
        r.GroqCleanupModel = CleanupModelBox.Text;
        _model.SaveSettings(); // пустые значения нормализатор вернёт к умолчаниям
        GeminiModelBox.Text = r.GeminiModel;
        WhisperModelBox.Text = r.GroqWhisperModel;
        CleanupModelBox.Text = r.GroqCleanupModel;
    }

    private void OnFallbackToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _model.Settings.Recognition.GroqFallbackEnabled = FallbackSwitch.IsOn;
        _model.SaveSettings();
    }

    private void OnCleanupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _model.Settings.Recognition.GroqCleanupEnabled = CleanupSwitch.IsOn;
        _model.SaveSettings();
    }

    private async void OnCheckKeys(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        _model.SaveSecrets();
        try
        {
            var r = _model.Settings.Recognition;
            CommitGeminiKeys();
            var keys = _model.Secrets.GeminiApiKeys;
            if (keys.Count == 0)
            {
                Show(GeminiInfo, "Ключ Gemini не задан.", InfoBarSeverity.Warning);
            }
            else
            {
                Show(GeminiInfo, "Gemini: проверяем…", InfoBarSeverity.Informational);
                var lines = new List<string>();
                var working = 0;
                for (var i = 0; i < keys.Count; i++)
                {
                    var name = keys.Count == 1 ? "Gemini" : $"Ключ {i + 1}";
                    var connection = new GeminiConnection(new Uri(r.GeminiEndpoint), keys[i],
                        new GeminiSessionOptions(r.GeminiModel, r.Mode, r.LanguageCodes, _model.Settings.Vocabulary));
                    var error = await GeminiKeyCheck.CheckAsync(new ClientWebSocketTransport(), connection, TimeSpan.FromSeconds(10));
                    if (error is { } kind)
                    {
                        lines.Add(ErrorMessages.Describe(kind, name));
                    }
                    else
                    {
                        working++;
                        lines.Add($"{name}: работает");
                    }
                }

                var severity = working == keys.Count ? InfoBarSeverity.Success
                    : working == 0 ? InfoBarSeverity.Error
                    : InfoBarSeverity.Warning;
                Show(GeminiInfo, string.Join(Environment.NewLine, lines), severity);
            }

            var groq = _model.Secrets.GroqApiKey;
            if (string.IsNullOrWhiteSpace(groq))
            {
                Show(GroqInfo, "Ключ Groq не задан — резерв не сработает.", InfoBarSeverity.Warning);
            }
            else
            {
                using var http = SettingsDictationEngines.CreateHttpClient();
                try
                {
                    await new GroqClient(http, r.GroqBaseUrl, groq).ValidateKeyAsync(CancellationToken.None);
                    Show(GroqInfo, "Groq: ключ работает.", InfoBarSeverity.Success);
                }
                catch (TranscriptionException ex)
                {
                    Show(GroqInfo, ErrorMessages.Describe(ex.Kind, "Groq"), InfoBarSeverity.Error);
                }
            }
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    private static void Show(InfoBar bar, string message, InfoBarSeverity severity)
    {
        bar.Message = message;
        bar.Severity = severity;
        bar.IsOpen = true;
    }
}
