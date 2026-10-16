using SaySo.Core.Diagnostics;
using SaySo.Core.Dictation;
using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;
using SaySo.Core.Engines.Groq;
using SaySo.Core.Settings;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Settings.Pages;

/// <summary>«Распознавание»: ключи Gemini (несколько), режим, язык, модель; резерв Groq; проверка ключей.</summary>
internal sealed class RecognitionPage : IPage
{
    private static readonly (string Label, string[] Codes)[] Languages =
    [
        ("Автоопределение", []),
        ("Русский (ru-RU)", ["ru-RU"]),
        ("English (en-US)", ["en-US"]),
    ];

    private readonly SettingsModel _model;
    private readonly Ui _ui = new();
    private readonly nint _geminiInfo;
    private readonly nint _groqInfo;
    private readonly nint _keyRows;
    private readonly List<(nint Row, nint Field)> _keys = [];
    private readonly nint _smart;
    private readonly nint _verbatim;
    private readonly nint _geminiModel;
    private readonly nint _groqKey;
    private readonly nint _whisperModel;
    private readonly nint _cleanupModel;
    private readonly nint _checkButton;

    public RecognitionPage(SettingsModel model)
    {
        _model = model;
        var r = model.Settings.Recognition;
        var (scroll, column) = Ui.ScrollPage();
        View = scroll;
        _geminiInfo = Ui.InfoLine();
        _groqInfo = Ui.InfoLine();

        _keyRows = Ui.Column(6);
        foreach (var key in model.Secrets.GeminiApiKeys)
        {
            AddKeyRow(key);
        }

        if (model.Secrets.GeminiApiKeys.Count == 0)
        {
            AddKeyRow("");
        }

        _smart = _ui.Radio("Умный: без слов-паразитов, с исправлением оговорок и пунктуацией", r.Mode == TranscriptionMode.Smart,
            () => SetMode(TranscriptionMode.Smart));
        _verbatim = _ui.Radio("Дословно: как сказано", r.Mode == TranscriptionMode.Verbatim, () => SetMode(TranscriptionMode.Verbatim));

        var labels = Languages.Select(l => l.Label).ToList();
        var language = Array.FindIndex(Languages, l => l.Codes.SequenceEqual(r.LanguageCodes, StringComparer.OrdinalIgnoreCase));
        if (language < 0)
        {
            labels.Add("Свои: " + string.Join(", ", r.LanguageCodes));
            language = labels.Count - 1;
        }

        var languages = _ui.PopUp(labels, language, OnLanguageChanged);
        _geminiModel = _ui.TextField(r.GeminiModel, RecognitionSettings.DefaultGeminiModel, _ => ApplyModelTexts(), _ => CommitModels());
        Ui.Width(_geminiModel, Ui.PageWidth - 2 * Ui.CardPadding);

        var fallback = _ui.Switch(r.GroqFallbackEnabled, on =>
        {
            if (on != _model.Settings.Recognition.GroqFallbackEnabled)
            {
                _model.Settings.Recognition.GroqFallbackEnabled = on;
                SaveSettingsOrWarn();
            }
        });
        _groqKey = _ui.TextField(model.Secrets.GroqApiKey ?? "", "gsk_…", text => _model.SetGroqKey(text), _ => SaveSecretsOrWarn(_groqInfo), secure: true);
        Ui.Width(_groqKey, Ui.PageWidth - 2 * Ui.CardPadding);
        var cleanup = _ui.Switch(r.GroqCleanupEnabled, on =>
        {
            if (on != _model.Settings.Recognition.GroqCleanupEnabled)
            {
                _model.Settings.Recognition.GroqCleanupEnabled = on;
                SaveSettingsOrWarn();
            }
        });
        _whisperModel = _ui.TextField(r.GroqWhisperModel, RecognitionSettings.DefaultGroqWhisperModel, _ => ApplyModelTexts(), _ => CommitModels());
        _cleanupModel = _ui.TextField(r.GroqCleanupModel, RecognitionSettings.DefaultGroqCleanupModel, _ => ApplyModelTexts(), _ => CommitModels());
        Ui.Width(_whisperModel, 400);
        Ui.Width(_cleanupModel, 400);
        _checkButton = _ui.AccentButton("Проверить ключи", () => _ = CheckKeysAsync());

        foreach (var view in new[]
        {
            Ui.Title("Распознавание"),
            _geminiInfo,
            _groqInfo,
            Ui.Label("Основной движок: Gemini 3.5 Transcribe", 13, bold: true),
            Ui.SettingBlock("Ключи Gemini API",
                "Можно добавить несколько ключей (например, от разных аккаунтов Google): когда у текущего кончается лимит, SaySo сам переключается на следующий, даже посреди фразы. Ключи создаются бесплатно в Google AI Studio (из России через VPN); на бесплатном тарифе Google может использовать запросы для улучшения своих продуктов.",
                _keyRows,
                Ui.Row(12, _ui.Button("Добавить ключ", () => FocusField(AddKeyRow(""))),
                    _ui.Button("Получить ключ в Google AI Studio", () => NS.Open("https://aistudio.google.com/apikey")))),
            Ui.SettingBlock("Режим", null, _smart, _verbatim),
            Ui.SettingRow("Язык речи", "Автоопределение справляется с русским и английскими терминами вперемешку.", languages, 220),
            Ui.SettingBlock("Модель", null, _geminiModel),
            Ui.Label("Резервный движок Groq (если Gemini недоступен)", 13, bold: true),
            Ui.SettingRow("Резерв через Groq Whisper", "Если у Gemini исчерпан лимит или нет связи, запись уйдёт в Groq, и сказанное не потеряется.", fallback, 50),
            Ui.SettingBlock("Ключ Groq API", null, _groqKey,
                _ui.Button("Получить ключ в Groq Console", () => NS.Open("https://console.groq.com/keys"))),
            Ui.SettingRow("Очистка текста LLM", "После Whisper текст дочищается моделью на Groq (паразиты, оговорки, пунктуация).", cleanup, 50),
            Ui.SettingBlock("Модели Groq", null,
                Ui.Row(12, Ui.Label("Распознавание", 13), Ui.Spacer(), _whisperModel),
                Ui.Row(12, Ui.Label("Очистка", 13), Ui.Spacer(), _cleanupModel)),
            _checkButton,
        })
        {
            Ui.Append(column, view);
        }
    }

    public string Title => "Распознавание";

    public string Symbol => "waveform";

    public nint View { get; }

    public void OnLeave()
    {
        // Значения уже в модели (вводятся сразу); запись на диск — Flush окна.
        SyncGeminiKeys();
        ApplyModelTexts();
    }

    public void Dispose() => _ui.Dispose();

    // Строка списка ключей: поле ключа + «Удалить». Значение уходит в модель сразу при вводе (чтобы не потерять ключ при
    // закрытии окна), на диск — при окончании ввода и при удалении.
    private nint AddKeyRow(string key, int insertAt = -1)
    {
        nint row = 0;
        var field = _ui.TextField(key, "AIza…", _ => SyncGeminiKeys(), _ => OnKeyCommitted(row), secure: true);
        Ui.Width(field, Ui.PageWidth - 2 * Ui.CardPadding - 100);
        var remove = _ui.Button("Удалить", () =>
        {
            var index = _keys.FindIndex(k => k.Row == row);
            if (index >= 0)
            {
                _keys.RemoveAt(index);
                Ui.Remove(_keyRows, row);
                CommitGeminiKeys();
            }
        });
        row = Ui.Row(8, field, remove);
        if (insertAt >= 0 && insertAt <= _keys.Count)
        {
            _keys.Insert(insertAt, (row, field));
            Ui.Insert(_keyRows, row, insertAt);
        }
        else
        {
            _keys.Add((row, field));
            Ui.Append(_keyRows, row);
        }

        return field;
    }

    private static void FocusField(nint field)
    {
        var window = ObjC.Send(field, ObjC.Sel("window"));
        if (window != 0)
        {
            ObjC.SendBool(window, ObjC.Sel("makeFirstResponder:"), field);
        }
    }

    // Вставленная строка «k1, k2» (через запятую, пробел или перевод строки) разбивается на отдельные строки ключей.
    private void OnKeyCommitted(nint row)
    {
        var index = _keys.FindIndex(k => k.Row == row);
        if (index < 0)
        {
            return; // строку уже удалили кнопкой «Удалить»: сохранение выполнил её обработчик
        }

        var parts = SettingsModel.SplitKeys(Ui.GetText(_keys[index].Field));
        if (parts.Length > 1)
        {
            Ui.SetText(_keys[index].Field, parts[0]);
            for (var i = 1; i < parts.Length; i++)
            {
                AddKeyRow(parts[i], index + i);
            }
        }

        CommitGeminiKeys();
    }

    private void SyncGeminiKeys() => _model.SetGeminiKeys(_keys.Select(k => Ui.GetText(k.Field)));

    private bool CommitGeminiKeys()
    {
        SyncGeminiKeys();
        return SaveSecretsOrWarn(_geminiInfo);
    }

    private bool SaveSecretsOrWarn(nint line)
    {
        if (_model.SaveSecrets())
        {
            return true;
        }

        Ui.Inform(line, SettingsModel.SaveFailedMessage, Severity.Error);
        return false;
    }

    private bool SaveSettingsOrWarn()
    {
        if (_model.SaveSettings())
        {
            return true;
        }

        Ui.Inform(_geminiInfo, SettingsModel.SaveFailedMessage, Severity.Error);
        return false;
    }

    private void SetMode(TranscriptionMode mode)
    {
        Ui.SetOn(_smart, mode == TranscriptionMode.Smart);
        Ui.SetOn(_verbatim, mode == TranscriptionMode.Verbatim);
        if (mode == _model.Settings.Recognition.Mode)
        {
            return;
        }

        _model.Settings.Recognition.Mode = mode;
        SaveSettingsOrWarn();
    }

    private void OnLanguageChanged(int index)
    {
        if (index < 0 || index >= Languages.Length)
        {
            return; // пункт «Свои: …» оставляет коды как есть
        }

        var codes = Languages[index].Codes;
        if (codes.SequenceEqual(_model.Settings.Recognition.LanguageCodes, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _model.Settings.Recognition.LanguageCodes = codes.ToList();
        SaveSettingsOrWarn();
    }

    private void ApplyModelTexts()
    {
        var r = _model.Settings.Recognition;
        var gemini = Ui.GetText(_geminiModel);
        var whisper = Ui.GetText(_whisperModel);
        var cleanup = Ui.GetText(_cleanupModel);
        if (gemini == r.GeminiModel && whisper == r.GroqWhisperModel && cleanup == r.GroqCleanupModel)
        {
            return;
        }

        r.GeminiModel = gemini;
        r.GroqWhisperModel = whisper;
        r.GroqCleanupModel = cleanup;
        _model.MarkSettingsDirty();
    }

    private void CommitModels()
    {
        ApplyModelTexts();
        SaveSettingsOrWarn(); // пустые значения нормализатор вернёт к умолчаниям
        ShowModelTexts();
    }

    private void ShowModelTexts()
    {
        var r = _model.Settings.Recognition;
        Ui.SetText(_geminiModel, r.GeminiModel);
        Ui.SetText(_whisperModel, r.GroqWhisperModel);
        Ui.SetText(_cleanupModel, r.GroqCleanupModel);
    }

    private async Task CheckKeysAsync()
    {
        Ui.Enable(_checkButton, false);
        try
        {
            CommitModels(); // нормализует пустые значения моделей до проверки
            _model.SetGroqKey(Ui.GetText(_groqKey));
            CommitGeminiKeys();
            var r = _model.Settings.Recognition;
            var keys = _model.Secrets.GeminiApiKeys;
            if (keys.Count == 0)
            {
                Ui.Inform(_geminiInfo, "Ключ Gemini не задан.", Severity.Warning);
            }
            else if (!TryParseGeminiEndpoint(r.GeminiEndpoint, out var endpoint))
            {
                Ui.Inform(_geminiInfo, ErrorMessages.Describe(EngineErrorKind.InvalidEndpoint, "Gemini"), Severity.Error);
            }
            else
            {
                Ui.Inform(_geminiInfo, "Gemini: проверяем…", Severity.Informational);
                var lines = new List<string>();
                var working = 0;
                for (var i = 0; i < keys.Count; i++)
                {
                    var name = keys.Count == 1 ? "Gemini" : $"Ключ {i + 1}";
                    var connection = new GeminiConnection(endpoint, keys[i],
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

                var severity = working == keys.Count ? Severity.Success : working == 0 ? Severity.Error : Severity.Warning;
                Ui.Inform(_geminiInfo, string.Join(Environment.NewLine, lines), severity);
            }

            var groq = _model.Secrets.GroqApiKey;
            if (string.IsNullOrWhiteSpace(groq))
            {
                Ui.Inform(_groqInfo, "Ключ Groq не задан, резерв не сработает.", Severity.Warning);
            }
            else
            {
                using var http = SettingsDictationEngines.CreateHttpClient();
                try
                {
                    await new GroqClient(http, r.GroqBaseUrl, groq).ValidateKeyAsync(CancellationToken.None);
                    Ui.Inform(_groqInfo, "Groq: ключ работает.", Severity.Success);
                }
                catch (TranscriptionException e)
                {
                    Ui.Inform(_groqInfo, ErrorMessages.Describe(e.Kind, "Groq"), Severity.Error);
                }
            }
        }
        catch (Exception e)
        {
            // Сообщение исключения не содержит ключей и уходит только в журнал.
            Log.Error("Проверка ключей", e);
            Ui.Inform(_geminiInfo, "Не удалось выполнить проверку, подробности в журнале", Severity.Error);
        }
        finally
        {
            Ui.Enable(_checkButton, true);
        }
    }

    private static bool TryParseGeminiEndpoint(string text, out Uri endpoint)
    {
        try
        {
            endpoint = GeminiConnection.ParseEndpoint(text);
            return true;
        }
        catch (TranscriptionException)
        {
            endpoint = null!;
            return false;
        }
    }
}
