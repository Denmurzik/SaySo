using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SaySo.Core.Hotkeys;
using SaySo.Core.Platform;

namespace SaySo.Settings.Pages;

public sealed partial class GeneralPage : UserControl
{
    private readonly SettingsModel _model;
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
        _loading = false;
    }

    private async void OnHotkeyClick(object sender, RoutedEventArgs e)
    {
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
            _model.SaveSettings();
            Show($"Горячая клавиша: {HotkeyFormatter.Format(keys)}", InfoBarSeverity.Success);
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

        _model.Settings.Hotkey.TapThresholdMs = ms;
        _model.SaveSettings();
    }

    private void OnSoundsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _model.Settings.SoundsEnabled = SoundsSwitch.IsOn;
        _model.SaveSettings();
    }

    private void OnAutostartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _model.Settings.AutostartEnabled = AutostartSwitch.IsOn;
        _model.SaveSettings();
    }

    private async void OnAdminToggled(object sender, RoutedEventArgs e)
    {
        if (_loading || _revertingAdmin)
        {
            return;
        }

        var enable = AdminSwitch.IsOn;
        var previous = _model.Settings.RunAsAdmin;
        AdminSwitch.IsEnabled = false;
        _model.Settings.RunAsAdmin = enable;
        _model.SaveSettings();
        var error = await AdminModeSwitcher.SetAsync(enable);
        if (error is not null)
        {
            _model.Settings.RunAsAdmin = previous;
            _model.SaveSettings();
            _revertingAdmin = true;
            AdminSwitch.IsOn = previous;
            _revertingAdmin = false;
            Show(error, InfoBarSeverity.Error);
        }
        else
        {
            Show(enable ? "SaySo перезапущен с правами администратора." : "SaySo перезапущен в обычном режиме.", InfoBarSeverity.Success);
        }

        UpdateAutostartAvailability();
        AdminSwitch.IsEnabled = true;
    }

    private void UpdateAutostartAvailability()
    {
        AutostartSwitch.IsEnabled = !_model.Settings.RunAsAdmin;
        AutostartHint.Text = _model.Settings.RunAsAdmin
            ? "В режиме администратора SaySo запускается задачей Планировщика при входе в систему."
            : "SaySo тихо живёт в области уведомлений.";
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        Info.Message = message;
        Info.Severity = severity;
        Info.IsOpen = true;
    }
}
