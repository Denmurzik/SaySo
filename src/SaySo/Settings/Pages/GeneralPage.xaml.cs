using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SaySo.Core.Diagnostics;
using SaySo.Core.Hotkeys;
using SaySo.Core.Platform;

namespace SaySo.Settings.Pages;

public sealed partial class GeneralPage : UserControl
{
    private static readonly TimeSpan TapSaveDelay = TimeSpan.FromMilliseconds(300);

    private readonly SettingsModel _model;
    private readonly DispatcherQueueTimer _tapSaveTimer;
    private bool _loading = true;
    private bool _revertingAdmin;

    internal GeneralPage(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        var s = model.Settings;
        HotkeyButton.Content = HotkeyFormatter.Format(s.Hotkey.Keys);
        TapSlider.Value = s.Hotkey.TapThresholdMs;
        TapValue.Text = $"{s.Hotkey.TapThresholdMs} мс";
        SoundsSwitch.IsOn = s.SoundsEnabled;
        AutostartSwitch.IsOn = s.AutostartEnabled;
        AdminSwitch.IsOn = s.RunAsAdmin;
        UpdateAutostartAvailability();
        _tapSaveTimer = DispatcherQueue.CreateTimer();
        _tapSaveTimer.Interval = TapSaveDelay;
        _tapSaveTimer.IsRepeating = false;
        _tapSaveTimer.Tick += (_, _) => SaveOrWarn();
        Unloaded += (_, _) => _tapSaveTimer.Stop(); // несохранённое уже в модели: его запишет Flush при переходе/закрытии
        _loading = false;
    }

    private async void OnHotkeyClick(object sender, RoutedEventArgs e)
    {
        if (HotkeyCapture.IsActive)
        {
            // Страницу пересоздали во время записи: хук в процессе один, вторую запись не начинаем.
            Show("Запись уже идёт.", InfoBarSeverity.Informational);
            return;
        }

        HotkeyButton.IsEnabled = false;
        HotkeyButton.Content = "Нажмите сочетание… (Esc — отмена)";
        IpcClient.Send(IpcProtocol.SuspendHotkey);
        try
        {
            var keys = await HotkeyCapture.CaptureAsync(TimeSpan.FromSeconds(10));
            if (keys is null)
            {
                return;
            }

            if (HotkeyValidator.Validate(keys.ToArray()) is { } problem)
            {
                Show(problem, InfoBarSeverity.Warning);
                return;
            }

            _model.Settings.Hotkey.Keys = keys.ToList();
            if (SaveOrWarn())
            {
                Show($"Горячая клавиша: {HotkeyFormatter.Format(keys)}", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Запись горячей клавиши", ex);
            Show("Не удалось записать сочетание: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IpcClient.Send(IpcProtocol.ResumeHotkey);
            HotkeyButton.Content = HotkeyFormatter.Format(_model.Settings.Hotkey.Keys);
            HotkeyButton.IsEnabled = true;
        }
    }

    private void OnTapChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        var ms = (int)Math.Round(e.NewValue / 50) * 50;
        if (TapValue is not null)
        {
            TapValue.Text = $"{ms} мс";
        }

        if (_loading)
        {
            return;
        }

        // Пока ползунок тянут, на диск не пишем: запись и ReloadSettings — через 300 мс после последнего шага.
        _model.Settings.Hotkey.TapThresholdMs = ms;
        _model.MarkSettingsDirty();
        _tapSaveTimer.Stop();
        _tapSaveTimer.Start();
    }

    private void OnSoundsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (SoundsSwitch.IsOn == _model.Settings.SoundsEnabled)
        {
            return; // Toggled может прийти после присвоения IsOn при открытии страницы
        }

        _model.Settings.SoundsEnabled = SoundsSwitch.IsOn;
        SaveOrWarn();
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (AutostartSwitch.IsOn == _model.Settings.AutostartEnabled)
        {
            return;
        }

        _model.Settings.AutostartEnabled = AutostartSwitch.IsOn;
        SaveOrWarn();
    }

    private async void OnAdminToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _revertingAdmin)
        {
            return;
        }

        var enable = AdminSwitch.IsOn;
        AdminSwitch.IsEnabled = false;
        try
        {
            // Режим записывается в настройки только после успешной установки/снятия задачи (внутри SetAsync).
            var error = await AdminModeSwitcher.SetAsync(enable, () => PersistAdminMode(enable));
            if (error is not null)
            {
                Show(error, InfoBarSeverity.Error);
            }
            else
            {
                Show(enable ? "SaySo перезапущен с правами администратора." : "SaySo перезапущен в обычном режиме.", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Переключение режима администратора", ex);
            Show("Не удалось переключить режим: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            // Переключатель показывает фактический (сохранённый) режим, а не то, что пользователь щёлкнул.
            _revertingAdmin = true;
            AdminSwitch.IsOn = _model.Settings.RunAsAdmin;
            _revertingAdmin = false;
            UpdateAutostartAvailability();
            AdminSwitch.IsEnabled = true;
        }
    }

    private bool PersistAdminMode(bool enable)
    {
        var previous = _model.Settings.RunAsAdmin;
        _model.Settings.RunAsAdmin = enable;
        if (_model.SaveSettings())
        {
            return true;
        }

        _model.Settings.RunAsAdmin = previous;
        return false;
    }

    private void UpdateAutostartAvailability()
    {
        AutostartSwitch.IsEnabled = !_model.Settings.RunAsAdmin;
        AutostartHint.Text = _model.Settings.RunAsAdmin
            ? "В режиме администратора SaySo запускается задачей Планировщика при входе в систему."
            : "SaySo тихо живёт в области уведомлений.";
    }

    private bool SaveOrWarn()
    {
        if (_model.SaveSettings())
        {
            return true;
        }

        Show(SettingsModel.SaveFailedMessage, InfoBarSeverity.Error);
        return false;
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        Info.Message = message;
        Info.Severity = severity;
        Info.IsOpen = true;
    }
}
