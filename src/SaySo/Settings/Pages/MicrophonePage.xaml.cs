using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Audio;
using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Diagnostics;

namespace SaySo.Settings.Pages;

public sealed partial class MicrophonePage : UserControl
{
    private readonly SettingsModel _model;
    private readonly DispatcherQueueTimer _timer;
    private WasapiCapture? _capture;
    private float _level;
    private bool _loading = true;
    private int _shownIndex = -1; // пункт, выбранный программно при открытии страницы

    internal MicrophonePage(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => LevelBar.Value = Volatile.Read(ref _level) * 100;
        _ = LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        IReadOnlyList<AudioDevice> devices;
        try
        {
            devices = await Task.Run(WasapiDevices.ListCaptureDevices);
        }
        catch (MicrophoneException e)
        {
            ShowError(e);
            devices = [];
        }
        catch (Exception e)
        {
            Log.Error("MicrophonePage.ListDevices", e);
            ShowMessage("Не удалось получить список микрофонов.", privacyLink: false);
            devices = [];
        }

        DevicesBox.Items.Add(new ComboBoxItem { Content = "Как в Windows (по умолчанию)", Tag = DeviceChoice.Default });
        foreach (var device in devices)
        {
            DevicesBox.Items.Add(new ComboBoxItem { Content = device.Name, Tag = new DeviceChoice(device.Id, device.Name) });
        }

        // Показываем то устройство, которое демон реально возьмёт: по идентификатору или (другой порт USB) по имени.
        // Если сохранённый микрофон отключён — отдельный пункт «<имя> — не подключён»: выбор виден и сам не сбрасывается.
        var settings = _model.Settings;
        var resolution = MicrophoneSelector.Resolve(settings.MicrophoneDeviceId, settings.MicrophoneDeviceName,
            devices.Select(d => new CaptureEndpoint(d.Id, d.Name, true)));
        var selected = 0;
        switch (resolution.Match)
        {
            case MicrophoneMatch.ById or MicrophoneMatch.ByName:
                selected = devices.ToList().FindIndex(d => d.Id == resolution.Endpoint!.Id) + 1;
                break;
            case MicrophoneMatch.Fallback:
                DevicesBox.Items.Add(new ComboBoxItem
                {
                    Content = string.IsNullOrWhiteSpace(settings.MicrophoneDeviceName)
                        ? "Сохранённый микрофон (сейчас недоступен)"
                        : $"{settings.MicrophoneDeviceName} — не подключён",
                    Tag = new DeviceChoice(settings.MicrophoneDeviceId, settings.MicrophoneDeviceName),
                });
                selected = DevicesBox.Items.Count - 1;
                break;
        }

        _shownIndex = selected;
        DevicesBox.SelectedIndex = selected;
        _loading = false;
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DevicesBox.SelectedItem is not ComboBoxItem { Tag: DeviceChoice choice })
        {
            return;
        }

        // SelectionChanged приходит и асинхронно после присвоения SelectedIndex при открытии: показанный пункт
        // (в т.ч. найденный по имени) — не выбор пользователя, сохранённое не трогаем.
        if (DevicesBox.SelectedIndex == _shownIndex)
        {
            return;
        }

        _shownIndex = -1;
        if (choice.Id == _model.Settings.MicrophoneDeviceId && choice.Name == _model.Settings.MicrophoneDeviceName)
        {
            return;
        }

        // Выбор запоминается вместе с именем; «как в Windows» очищает оба значения.
        _model.Settings.MicrophoneDeviceId = choice.Id;
        _model.Settings.MicrophoneDeviceName = choice.Name;
        var saved = _model.SaveSettings();
        if (TestButton.IsChecked == true)
        {
            StopTest();
            _ = StartTestAsync(); // закрывает InfoBar — сообщение об ошибке сохранения показываем после
        }

        if (!saved)
        {
            ShowMessage(SettingsModel.SaveFailedMessage, privacyLink: false);
        }
    }

    private void OnTestChecked(object sender, RoutedEventArgs e) => _ = StartTestAsync();

    private void OnTestUnchecked(object sender, RoutedEventArgs e) => StopTest();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        StopTest();
        TestButton.IsChecked = false; // Unchecked → StopTest (идемпотентно): кнопка не остаётся нажатой без захвата
    }

    /// <summary>Остановка проверки при закрытии окна.</summary>
    internal void Shutdown() => StopTest();

    private async Task StartTestAsync()
    {
        Info.IsOpen = false;
        WasapiCapture? capture = null;

        // Ошибка приходит из потока захвата: Stop() (join потока) здесь вызывать нельзя —
        // переносим обработку в UI-поток; устаревшие ошибки (после остановки/смены устройства) игнорируем.
        capture = new WasapiCapture(_model.Settings.MicrophoneDeviceId,
            data => Volatile.Write(ref _level, LevelMeter.Compute(data)),
            e => DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_capture, capture))
                {
                    return;
                }

                ShowError(e);
                TestButton.IsChecked = false;
            }),
            _model.Settings.MicrophoneDeviceName);
        _capture = capture;
        try
        {
            await capture.StartAsync();
            if (ReferenceEquals(_capture, capture))
            {
                _timer.Start();
            }
        }
        catch (Exception e)
        {
            if (!ReferenceEquals(_capture, capture))
            {
                return; // проверку уже остановили
            }

            if (e is MicrophoneException mic)
            {
                ShowError(mic);
            }
            else
            {
                Log.Error("MicrophonePage.StartTest", e);
                ShowMessage("Не удалось запустить проверку микрофона.", privacyLink: false);
            }

            StopTest();
            TestButton.IsChecked = false;
        }
    }

    private void StopTest()
    {
        _timer.Stop();
        var capture = _capture;
        _capture = null;
        capture?.Dispose();
        Volatile.Write(ref _level, 0f);
        LevelBar.Value = 0;
    }

    private void ShowError(MicrophoneException e) =>
        ShowMessage(e.UserMessage, privacyLink: e.Kind == MicrophoneErrorKind.AccessDenied);

    private void ShowMessage(string message, bool privacyLink)
    {
        Info.Message = message;
        Info.Severity = InfoBarSeverity.Error;
        PrivacyLink.Visibility = privacyLink ? Visibility.Visible : Visibility.Collapsed;
        Info.IsOpen = true;
    }

    private async void OnOpenPrivacy(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:privacy-microphone"));
        }
        catch (Exception ex)
        {
            Log.Error("MicrophonePage.OpenPrivacy", ex);
        }
    }

    /// <summary>Выбор устройства в списке: идентификатор и имя сохраняются вместе (оба null — как в Windows).</summary>
    private sealed record DeviceChoice(string? Id, string? Name)
    {
        public static DeviceChoice Default { get; } = new(null, null);
    }
}
