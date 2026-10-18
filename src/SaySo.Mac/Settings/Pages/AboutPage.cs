using SaySo.Core.Diagnostics;
using SaySo.Core.Platform;
using SaySo.Core.Updates;
using SaySo.Mac.Interop;
using SaySo.Mac.Platform;

namespace SaySo.Mac.Settings.Pages;

/// <summary>«О программе»: версия, проверка обновлений (на macOS — страница релиза), как получить ключ, конфиденциальность.</summary>
internal sealed class AboutPage : IPage
{
    private static readonly Version CurrentVersion = typeof(AboutPage).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private readonly SettingsModel _model;
    private readonly Ui _ui = new();
    private readonly nint _status;
    private readonly nint _notes;
    private readonly nint _checkButton;
    private readonly nint _openButton;
    private readonly nint _spinner;
    private ReleaseInfo? _release;
    private bool _busy;

    public AboutPage(SettingsModel model)
    {
        _model = model;
        var (scroll, column) = Ui.ScrollPage();
        View = scroll;
        var width = Ui.PageWidth - 2 * Ui.CardPadding;
        _status = Ui.Label("Нажмите «Проверить обновления».", 12, secondary: true, wrapWidth: width);
        _notes = Ui.Label("", 12, secondary: true, wrapWidth: width);
        Ui.Show(_notes, false);
        _checkButton = _ui.Button("Проверить обновления", () => _ = CheckAsync());
        _openButton = _ui.AccentButton("Открыть страницу релиза", () =>
        {
            if (_release is { } release)
            {
                UpdateShell.OpenUrl(release.HtmlUrl);
            }
        });
        Ui.Show(_openButton, false);
        _spinner = ObjC.Send(ObjC.New("NSProgressIndicator"), ObjC.Sel("autorelease"));
        ObjC.Send(_spinner, ObjC.Sel("setStyle:"), (nint)1); // NSProgressIndicatorStyleSpinning
        ObjC.Send(_spinner, ObjC.Sel("setControlSize:"), (nint)1); // NSControlSizeSmall
        ObjC.SendVoid(_spinner, ObjC.Sel("setDisplayedWhenStopped:"), false);

        foreach (var view in new[]
        {
            Ui.Title("SaySo"),
            Ui.Label($"Версия {CurrentVersion.ToString(3)} · лицензия GPL-3.0-or-later", 12, secondary: true),
            Ui.Label("Зажмите клавишу, говорите, отпустите: очищенный текст появится в активном окне. Распознавание: Gemini 3.5 Transcribe (режим SMART), резервный движок Groq Whisper.",
                13, wrapWidth: Ui.PageWidth),
            Ui.SettingBlock("Обновления", null, _status, _notes, Ui.Row(12, _checkButton, _openButton, _spinner)),
            Ui.SettingBlock("Как получить ключ Gemini",
                "1. Откройте Google AI Studio (из России с включённым VPN) и войдите в аккаунт Google.\n2. Нажмите «Create API key», скопируйте ключ и вставьте в «Распознавание».\n3. Свои лимиты бесплатного тарифа смотрите в AI Studio → Rate limits: Google не публикует общих цифр для этой модели.",
                _ui.Button("Ключи Google AI Studio", () => NS.Open("https://aistudio.google.com/apikey"))),
            Ui.SettingBlock("Конфиденциальность",
                "Аудио не сохраняется на диск. Аудио и термины словаря отправляются в Google Gemini; если сработал резерв, аудио, расшифровка и термины словаря уходят в Groq; проверка ключей тоже обращается к этим API. Проверка обновлений обращается к GitHub: уходит только запрос о последнем релизе, ключи и тексты не отправляются. Ключи и история хранятся локально, зашифрованы ключом из Связки ключей macOS. На бесплатном тарифе Gemini Google может использовать запросы для улучшения своих продуктов."),
            Ui.Row(12,
                _ui.Button("Исходный код на GitHub", () => NS.Open("https://github.com/Denmurzik/SaySo")),
                _ui.Button("Папка данных и журнал", () => NS.OpenFolder(_model.Paths.Root))),
        })
        {
            Ui.Append(column, view);
        }
    }

    public string Title => "О программе";

    public string Symbol => "info.circle";

    public nint View { get; }

    public void OnLeave()
    {
    }

    public void Dispose() => _ui.Dispose();

    // Окно настроек проверяет само тем же кодом, что и демон, чтобы показать результат; демону отдельно сообщается о находке
    // (пункт меню значка «Страница релиза X.Y.Z»).
    private async Task CheckAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true, "Проверяю…");
        try
        {
            var runner = new UpdateCheckRunner(new UpdateStateStore(_model.Paths), CurrentVersion, UpdateHttp.Create, TimeProvider.System);
            ShowResult(await runner.RunAsync());
        }
        catch (Exception e)
        {
            Log.Error("AboutPage.CheckUpdates", e);
            SetBusy(false, "Не удалось проверить обновления, подробности в журнале");
        }
    }

    private void ShowResult(UpdateCheckResult result)
    {
        _release = null;
        Ui.Show(_notes, false);
        Ui.Show(_openButton, false);
        switch (result.Outcome)
        {
            case UpdateCheckOutcome.Failed:
                SetBusy(false, UpdateMessages.CheckFailed(result.Failure ?? UpdateFailure.BadResponse));
                break;
            case UpdateCheckOutcome.Available:
                var release = result.Release!;
                _release = release;
                Ui.Show(_openButton, true);
                if (release.Notes.Length > 0)
                {
                    Ui.SetText(_notes, release.Notes);
                    Ui.Show(_notes, true);
                }

                Ipc.Send(IpcProtocol.CheckUpdates); // пункт «Страница релиза X.Y.Z» в меню значка
                SetBusy(false, UpdateMessages.Available(release.Version, manual: true));
                break;
            default:
                SetBusy(false, UpdateMessages.UpToDate);
                break;
        }
    }

    private void SetBusy(bool busy, string status)
    {
        _busy = busy;
        Ui.SetText(_status, status);
        Ui.Enable(_checkButton, !busy);
        ObjC.SendVoid(_spinner, ObjC.Sel(busy ? "startAnimation:" : "stopAnimation:"), (nint)0);
    }
}
