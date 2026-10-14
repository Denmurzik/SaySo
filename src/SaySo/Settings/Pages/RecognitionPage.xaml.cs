using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Dictation;
using SaySo.Core.Diagnostics;
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

    private static readonly char[] KeySeparators = [',', ';', ' ', '\t', '\r', '\n'];

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

    // Строка списка ключей: поле ключа + «Удалить». Значение уходит в модель сразу при вводе (чтобы не потерять
    // ключ при закрытии окна), на диск — при потере фокуса и при удалении.
    private PasswordBox AddGeminiKeyRow(string key, int insertAt = -1)
    {
        var box = new PasswordBox { Password = key, PasswordRevealMode = PasswordRevealMode.Peek, PlaceholderText = "AIza…" };
        box.PasswordChanged += (_, _) => SyncGeminiKeys();
        box.LostFocus += (_, _) => OnGeminiKeyLostFocus(box);
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
        if (insertAt >= 0 && insertAt <= GeminiKeysPanel.Children.Count)
        {
            GeminiKeysPanel.Children.Insert(insertAt, row);
        }
        else
        {
            GeminiKeysPanel.Children.Add(row);
        }

        return box;
    }

    // Вставленная строка «k1, k2» (через запятую, пробел или перевод строки) разбивается на отдельные строки ключей.
    private void OnGeminiKeyLostFocus(PasswordBox box)
    {
        if (_loading)
        {
            return;
        }

        var parts = box.Password.Split(KeySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 1 && box.Parent is Grid row)
        {
            var index = GeminiKeysPanel.Children.IndexOf(row);
            box.Password = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                AddGeminiKeyRow(parts[i], index + i);
            }
        }

        CommitGeminiKeys();
    }

    private void SyncGeminiKeys()
    {
        if (_loading)
        {
            return;
        }

        _model.SetGeminiKeys(GeminiKeysPanel.Children.OfType<Grid>()
            .Select(row => row.Children.OfType<PasswordBox>().First().Password));
    }

    private bool CommitGeminiKeys()
    {
        SyncGeminiKeys();
        return SaveSecretsOrWarn(GeminiInfo);
    }

    private bool SaveSecretsOrWarn(InfoBar bar)
    {
        if (_model.SaveSecrets())
        {
            return true;
        }

        Show(bar, SettingsModel.SaveFailedMessage, InfoBarSeverity.Error);
        return false;
    }

    private bool SaveSettingsOrWarn()
    {
        if (_model.SaveSettings())
        {
            return true;
        }

        Show(GeminiInfo, SettingsModel.SaveFailedMessage, InfoBarSeverity.Error);
        return false;
    }

    private void OnGroqKeyChanged(object sender, RoutedEventArgs e)
    {
        if (!_loading)
        {
            _model.SetGroqKey(GroqKeyBox.Password);
        }
    }

    private void OnKeyLostFocus(object sender, RoutedEventArgs e) => SaveSecretsOrWarn(GroqInfo);

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _model.Settings.Recognition.Mode = ModeButtons.SelectedIndex == 1 ? TranscriptionMode.Verbatim : TranscriptionMode.Smart;
        SaveSettingsOrWarn();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageBox.SelectedIndex < 0 || LanguageBox.SelectedIndex >= Languages.Length)
        {
            return; // пункт «Свои: …» оставляет коды как есть
        }

        _model.Settings.Recognition.LanguageCodes = Languages[LanguageBox.SelectedIndex].Codes.ToList();
        SaveSettingsOrWarn();
    }

    // Текстовые поля моделей: значение уходит в модель сразу при вводе (запись — при потере фокуса и закрытии окна).
    private void OnModelTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ApplyModelTexts();
        _model.MarkSettingsDirty();
    }

    private void ApplyModelTexts()
    {
        var r = _model.Settings.Recognition;
        r.GeminiModel = GeminiModelBox.Text;
        r.GroqWhisperModel = WhisperModelBox.Text;
        r.GroqCleanupModel = CleanupModelBox.Text;
    }

    private void OnModelLostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        ApplyModelTexts();
        SaveSettingsOrWarn(); // пустые значения нормализатор вернёт к умолчаниям
        ShowModelTexts();
    }

    private void ShowModelTexts()
    {
        var r = _model.Settings.Recognition;
        _loading = true; // присвоение Text вызвало бы TextChanged и снова пометило бы настройки несохранёнными
        GeminiModelBox.Text = r.GeminiModel;
        WhisperModelBox.Text = r.GroqWhisperModel;
        CleanupModelBox.Text = r.GroqCleanupModel;
        _loading = false;
    }

    private void OnFallbackToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _model.Settings.Recognition.GroqFallbackEnabled = FallbackSwitch.IsOn;
        SaveSettingsOrWarn();
    }

    private void OnCleanupToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _model.Settings.Recognition.GroqCleanupEnabled = CleanupSwitch.IsOn;
        SaveSettingsOrWarn();
    }

    private async void OnCheckKeys(object sender, RoutedEventArgs e)
    {
        CheckButton.IsEnabled = false;
        try
        {
            var r = _model.Settings.Recognition;
            ApplyModelTexts();
            SaveSettingsOrWarn(); // нормализует пустые значения моделей до проверки
            ShowModelTexts();
            _model.SetGroqKey(GroqKeyBox.Password);
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
        catch (Exception ex)
        {
            // Сообщение исключения не содержит ключей: они передаются заголовком, а не в адресе.
            Log.Error("Проверка ключей", ex);
            Show(GeminiInfo, "Не удалось выполнить проверку: " + ex.Message, InfoBarSeverity.Error);
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
