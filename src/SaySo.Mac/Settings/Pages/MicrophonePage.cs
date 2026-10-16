using SaySo.Core.Audio;
using SaySo.Core.Audio.Wasapi;
using SaySo.Core.Diagnostics;
using SaySo.Mac.Interop;
using SaySo.Mac.Platform;

namespace SaySo.Mac.Settings.Pages;

/// <summary>«Микрофон»: выбор устройства (запоминается по UID и имени) и проверка уровня.</summary>
internal sealed class MicrophonePage : IPage
{
    private readonly SettingsModel _model;
    private readonly Ui _ui = new();
    private readonly nint _info;
    private readonly nint _privacyButton;
    private readonly nint _devices;
    private readonly nint _level;
    private readonly nint _testButton;
    private readonly List<DeviceChoice> _choices = [];
    private AudioCapture? _capture;
    private IDisposable? _timer;
    private float _levelValue;

    public MicrophonePage(SettingsModel model)
    {
        _model = model;
        var (scroll, column) = Ui.ScrollPage();
        View = scroll;
        _info = Ui.InfoLine();
        _privacyButton = _ui.Button("Открыть настройки конфиденциальности", () => NS.Open(Accessibility.MicrophoneSettingsUrl));
        Ui.Show(_privacyButton, false);
        _devices = _ui.PopUp([], 0, OnDeviceChanged);
        Ui.Width(_devices, 280);
        _level = ObjC.Send(ObjC.New("NSProgressIndicator"), ObjC.Sel("autorelease"));
        ObjC.SendVoid(_level, ObjC.Sel("setIndeterminate:"), false);
        ObjC.SendVoid(_level, ObjC.Sel("setMinValue:"), 0.0);
        ObjC.SendVoid(_level, ObjC.Sel("setMaxValue:"), 100.0);
        Ui.Width(_level, Ui.PageWidth - 2 * Ui.CardPadding);
        _testButton = _ui.Button("Проверить микрофон", () =>
        {
            if (_capture is null)
            {
                _ = StartTestAsync();
            }
            else
            {
                StopTest();
            }
        });

        foreach (var view in new[]
        {
            Ui.Title("Микрофон"),
            _info,
            _privacyButton,
            Ui.SettingRow("Устройство записи",
                "Выбранный микрофон используется всегда, когда подключён; иначе берётся микрофон macOS по умолчанию.", _devices, 280),
            Ui.SettingBlock("Проверка", "Скажите что-нибудь: полоска должна двигаться.", _level, _testButton),
        })
        {
            Ui.Append(column, view);
        }

        LoadDevices();
    }

    public string Title => "Микрофон";

    public string Symbol => "mic";

    public nint View { get; }

    public void OnLeave() => StopTest();

    public void Dispose()
    {
        StopTest();
        _ui.Dispose();
    }

    private void LoadDevices()
    {
        IReadOnlyList<AudioDevice> devices;
        try
        {
            devices = AudioDevices.ListCaptureDevices();
        }
        catch (Exception e)
        {
            Log.Error("MicrophonePage.ListDevices", e);
            ShowMessage("Не удалось получить список микрофонов.", privacyLink: false);
            devices = [];
        }

        _choices.Add(DeviceChoice.Default);
        Ui.AddItem(_devices, "Как в macOS (по умолчанию)");
        foreach (var device in devices)
        {
            _choices.Add(new DeviceChoice(device.Id, MicrophoneSelector.NameToRemember(device.Name)));
            Ui.AddItem(_devices, device.Name);
        }

        // Показываем то устройство, которое демон реально возьмёт: по UID или (новый UID) по имени. Если сохранённый
        // микрофон отключён — отдельный пункт «<имя> (не подключён)»: выбор виден и сам не сбрасывается.
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
                _choices.Add(new DeviceChoice(settings.MicrophoneDeviceId, settings.MicrophoneDeviceName));
                Ui.AddItem(_devices, string.IsNullOrWhiteSpace(settings.MicrophoneDeviceName)
                    ? "Сохранённый микрофон (сейчас недоступен)"
                    : $"{settings.MicrophoneDeviceName} (не подключён)");
                selected = _choices.Count - 1;
                break;
        }

        Ui.Select(_devices, selected);
    }

    private void OnDeviceChanged(int index)
    {
        if (index < 0 || index >= _choices.Count)
        {
            return;
        }

        var choice = _choices[index];
        if (choice.Id == _model.Settings.MicrophoneDeviceId && choice.Name == _model.Settings.MicrophoneDeviceName)
        {
            return;
        }

        // Выбор запоминается вместе с именем; «как в macOS» очищает оба значения.
        _model.Settings.MicrophoneDeviceId = choice.Id;
        _model.Settings.MicrophoneDeviceName = choice.Name;
        var saved = _model.SaveSettings();
        if (_capture is not null)
        {
            StopTest();
            _ = StartTestAsync();
        }

        if (!saved)
        {
            ShowMessage(SettingsModel.SaveFailedMessage, privacyLink: false);
        }
    }

    private async Task StartTestAsync()
    {
        Ui.Inform(_info, "", Severity.Informational);
        Ui.Show(_privacyButton, false);
        AudioCapture? capture = null;
        capture = new AudioCapture(_model.Settings.MicrophoneDeviceId,
            data => Volatile.Write(ref _levelValue, LevelMeter.Compute(data)),
            e => Dispatch.Post(() =>
            {
                if (ReferenceEquals(_capture, capture))
                {
                    ShowError(e);
                    StopTest();
                }
            }),
            _model.Settings.MicrophoneDeviceName,
            requestPermission: true);
        _capture = capture;
        Ui.SetTitle(_testButton, "Остановить проверку");
        try
        {
            await capture.StartAsync();
            if (ReferenceEquals(_capture, capture))
            {
                _timer = Dispatch.Every(TimeSpan.FromMilliseconds(50),
                    () => ObjC.SendVoid(_level, ObjC.Sel("setDoubleValue:"), Volatile.Read(ref _levelValue) * 100.0));
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
        }
    }

    private void StopTest()
    {
        _timer?.Dispose();
        _timer = null;
        var capture = _capture;
        _capture = null;
        capture?.Dispose();
        Volatile.Write(ref _levelValue, 0f);
        ObjC.SendVoid(_level, ObjC.Sel("setDoubleValue:"), 0.0);
        Ui.SetTitle(_testButton, "Проверить микрофон");
    }

    private void ShowError(MicrophoneException e) => ShowMessage(e.Detail, privacyLink: e.Kind == MicrophoneErrorKind.AccessDenied);

    private void ShowMessage(string message, bool privacyLink)
    {
        Ui.Inform(_info, message, Severity.Error);
        Ui.Show(_privacyButton, privacyLink);
    }

    /// <summary>Пункт списка: UID и имя сохраняются вместе (оба null — как в macOS).</summary>
    private sealed record DeviceChoice(string? Id, string? Name)
    {
        public static DeviceChoice Default { get; } = new(null, null);
    }
}
