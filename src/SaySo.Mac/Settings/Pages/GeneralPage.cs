using SaySo.Core.Audio;
using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Core.Platform;
using SaySo.Mac.Interop;
using SaySo.Mac.Platform;

namespace SaySo.Mac.Settings.Pages;

/// <summary>«Общие»: горячая клавиша, порог короткого нажатия, звуки, автозапуск, обновления и разрешения macOS.</summary>
internal sealed class GeneralPage : IPage
{
    private static readonly TimeSpan TapSaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly SettingsModel _model;
    private readonly Ui _ui = new();
    private readonly nint _info;
    private readonly nint _hotkeyButton;
    private readonly nint _tapValue;
    private readonly nint _themes;
    private readonly nint _accessibilityStatus;
    private readonly nint _microphoneStatus;
    private IDisposable? _tapSaveTimer;
    private IDisposable? _permissionsTimer;

    public GeneralPage(SettingsModel model)
    {
        _model = model;
        var s = model.Settings;
        var (scroll, column) = Ui.ScrollPage();
        View = scroll;
        _info = Ui.InfoLine();

        _hotkeyButton = _ui.Button(HotkeyFormatter.Format(s.Hotkey.Keys), () => _ = CaptureHotkeyAsync());
        Ui.Width(_hotkeyButton, 200);

        _tapValue = Ui.Label($"{s.Hotkey.TapThresholdMs} мс", 13);
        Ui.Width(_tapValue, 60);
        var slider = _ui.Slider(150, 1000, s.Hotkey.TapThresholdMs, 50, OnTapChanged);

        var sounds = _ui.Switch(s.SoundsEnabled, on =>
        {
            if (on != _model.Settings.SoundsEnabled)
            {
                _model.Settings.SoundsEnabled = on;
                SaveOrWarn();
            }
        });
        var selected = SoundThemes.All.ToList().FindIndex(t => t.Id == SoundThemes.Normalize(s.SoundTheme));
        _themes = _ui.PopUp(SoundThemes.All.Select(t => t.Name), selected, OnThemeChanged);
        var preview = _ui.Button("Прослушать", () =>
        {
            var index = Ui.SelectedIndex(_themes);
            if (index >= 0 && index < SoundThemes.All.Count)
            {
                MacSoundPlayer.Play(SoundKind.Start, SoundThemes.All[index].Id);
            }
        });

        var autostart = _ui.Switch(s.AutostartEnabled, on =>
        {
            if (on != _model.Settings.AutostartEnabled)
            {
                _model.Settings.AutostartEnabled = on;
                SaveOrWarn();
            }
        });
        var updates = _ui.Switch(s.CheckForUpdates, on =>
        {
            if (on != _model.Settings.CheckForUpdates)
            {
                _model.Settings.CheckForUpdates = on;
                SaveOrWarn();
            }
        });

        _accessibilityStatus = Ui.Label("", 12, secondary: true);
        _microphoneStatus = Ui.Label("", 12, secondary: true);
        var permissions = Ui.SettingBlock("Разрешения macOS",
            "Универсальный доступ нужен, чтобы SaySo видел горячую клавишу и вставлял текст (Cmd+V) в любое приложение. " +
            "Микрофон нужен для записи. Разрешения выдаются в Системных настройках, в разделе «Конфиденциальность и безопасность».",
            Ui.Row(12, _accessibilityStatus, Ui.Spacer(), _ui.Button("Открыть настройки", () =>
            {
                Accessibility.Prompt();
                NS.Open(Accessibility.SettingsUrl);
            })),
            Ui.Row(12, _microphoneStatus, Ui.Spacer(), _ui.Button("Открыть настройки", OpenMicrophoneSettings)));

        foreach (var view in new[]
        {
            Ui.Title("Общие"),
            _info,
            Ui.SettingRow("Горячая клавиша",
                "Пока клавиша удерживается, идёт запись; отпустите, и текст вставится. Короткое нажатие включает режим «руки свободны», повторное завершает запись. Отмена: Esc.",
                _hotkeyButton, 200),
            Ui.SettingRow("Короткое нажатие", "Нажатие короче этого порога включает режим «руки свободны».",
                Ui.Row(8, slider, _tapValue), 290),
            Ui.SettingBlock("Звуковые сигналы", "Короткие сигналы начала и конца записи и ошибки.",
                Ui.Row(12, Ui.Label("Сигналы", 13), Ui.Spacer(), sounds),
                Ui.Row(12, Ui.Label("Звуки", 13), Ui.Spacer(), _themes, preview)),
            Ui.SettingRow("Открывать при входе в систему", "SaySo тихо живёт в строке меню.", autostart, 50),
            Ui.SettingRow("Проверять обновления автоматически",
                "Раз в сутки SaySo спрашивает у GitHub, нет ли новой версии. Ключи и тексты диктовок не отправляются. Проверить вручную можно на странице «О программе».",
                updates, 50),
            permissions,
        })
        {
            Ui.Append(column, view);
        }

        RefreshPermissions();
        // Пока страница открыта, состояние разрешений обновляется: пользователь выдаёт их в Системных настройках.
        _permissionsTimer = Dispatch.Every(TimeSpan.FromSeconds(1), RefreshPermissions);
    }

    public string Title => "Общие";

    public string Symbol => "gearshape";

    public nint View { get; }

    public void OnLeave()
    {
        HotkeyCapture.CancelActive();
        _tapSaveTimer?.Dispose();
        _tapSaveTimer = null;
    }

    public void Dispose()
    {
        OnLeave();
        _permissionsTimer?.Dispose();
        _ui.Dispose();
    }

    private async Task CaptureHotkeyAsync()
    {
        if (HotkeyCapture.IsActive)
        {
            Ui.Inform(_info, "Запись уже идёт.", Severity.Informational);
            return;
        }

        Ui.Enable(_hotkeyButton, false);
        Ui.SetTitle(_hotkeyButton, "Нажмите сочетание… (Esc: отмена)");
        Ipc.Send(IpcProtocol.SuspendHotkey);
        try
        {
            var keys = await HotkeyCapture.CaptureAsync(TimeSpan.FromSeconds(10));
            if (keys is null)
            {
                return;
            }

            if (HotkeyValidator.Validate(keys.ToArray()) is { } problem)
            {
                Ui.Inform(_info, problem, Severity.Warning);
                return;
            }

            _model.Settings.Hotkey.Keys = keys.ToList();
            if (SaveOrWarn())
            {
                Ui.Inform(_info, $"Горячая клавиша: {HotkeyFormatter.Format(keys)}", Severity.Success);
            }
        }
        catch (Exception e)
        {
            Log.Error("Запись горячей клавиши", e);
            Ui.Inform(_info, "Не удалось записать сочетание, подробности в журнале", Severity.Error);
        }
        finally
        {
            Ipc.Send(IpcProtocol.ResumeHotkey);
            Ui.SetTitle(_hotkeyButton, HotkeyFormatter.Format(_model.Settings.Hotkey.Keys));
            Ui.Enable(_hotkeyButton, true);
        }
    }

    private void OnTapChanged(double value)
    {
        var ms = (int)Math.Round(value / 50) * 50;
        Ui.SetText(_tapValue, $"{ms} мс");
        if (ms == _model.Settings.Hotkey.TapThresholdMs)
        {
            return;
        }

        // Пока ползунок тянут, на диск не пишем: запись и ReloadSettings — через 300 мс после последнего шага.
        _model.Settings.Hotkey.TapThresholdMs = ms;
        _model.MarkSettingsDirty();
        _tapSaveTimer?.Dispose();
        _tapSaveTimer = Dispatch.After(TapSaveDelay, () => SaveOrWarn());
    }

    private void OnThemeChanged(int index)
    {
        if (index < 0 || index >= SoundThemes.All.Count || SoundThemes.All[index].Id == _model.Settings.SoundTheme)
        {
            return;
        }

        _model.Settings.SoundTheme = SoundThemes.All[index].Id;
        SaveOrWarn();
    }

    private void RefreshPermissions()
    {
        Ui.SetText(_accessibilityStatus, Accessibility.IsTrusted
            ? "Универсальный доступ: разрешён"
            : "Универсальный доступ: не разрешён, горячая клавиша не работает");
        Ui.SetText(_microphoneStatus, MicrophonePermission.Current() switch
        {
            MicrophonePermission.Status.Authorized => "Микрофон: разрешён",
            MicrophonePermission.Status.NotDetermined => "Микрофон: SaySo спросит при первой записи",
            _ => "Микрофон: не разрешён",
        });
    }

    private static void OpenMicrophoneSettings()
    {
        if (MicrophonePermission.Current() == MicrophonePermission.Status.NotDetermined)
        {
            _ = MicrophonePermission.RequestAsync();
            return;
        }

        NS.Open(Accessibility.MicrophoneSettingsUrl);
    }

    private bool SaveOrWarn()
    {
        if (_model.SaveSettings())
        {
            return true;
        }

        Ui.Inform(_info, SettingsModel.SaveFailedMessage, Severity.Error);
        return false;
    }
}

/// <summary>Страница окна настроек.</summary>
internal interface IPage : IDisposable
{
    string Title { get; }

    /// <summary>Значок SF Symbols для панели инструментов.</summary>
    string Symbol { get; }

    nint View { get; }

    /// <summary>Уход со страницы или закрытие окна: записать несохранённое и остановить то, что идёт.</summary>
    void OnLeave();
}
