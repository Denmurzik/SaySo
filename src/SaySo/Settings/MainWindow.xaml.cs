using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Diagnostics;
using SaySo.Core.Interop;
using SaySo.Core.Platform;
using SaySo.Settings.Pages;

namespace SaySo.Settings;

public sealed partial class MainWindow : Window
{
    private const int MinWidthDip = 760;
    private const int MinHeightDip = 520;

    private readonly SettingsModel _model = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = SettingsWindowInfo.Title;
        AppTitleText.Text = SettingsWindowInfo.Title;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(980 * scale), (int)(720 * scale)));
        // Меньше этого закреплённая панель и карточки не помещаются (контент обрезается).
        WindowMinSize.Apply(hwnd, MinWidthDip, MinHeightDip);
        Closed += (_, _) => ShutDownSettings();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        // Правки словаря попадают в модель до Flush: если файл занят, предупреждение ниже покажет и их.
        (PageHost.Content as DictionaryPage)?.SaveIfChanged();
        HotkeyCapture.CancelActive(); // запись горячей клавиши принадлежит странице, которую сейчас заменят
        // Не записали (файл занят) — предупреждаем здесь: у страницы, которую заменяем, уже не спросить.
        SaveInfo.Message = SettingsModel.SaveFailedMessage;
        SaveInfo.IsOpen = !_model.Flush();
        PageHost.Content = tag switch
        {
            "general" => new GeneralPage(_model),
            "recognition" => new RecognitionPage(_model),
            "dictionary" => new DictionaryPage(_model),
            "microphone" => new MicrophonePage(_model),
            "history" => new HistoryPage(_model),
            "about" => new AboutPage(_model),
            _ => null,
        };
    }

    // Завершение окна — отдельным блоком, чтобы задача 18 добавила сюда свои шаги (остановка теста микрофона) без конфликтов.
    private void ShutDownSettings()
    {
        // Unloaded у страниц при закрытии окна не гарантирован — их шаги выполняем явно и первыми.
        (PageHost.Content as MicrophonePage)?.Shutdown(); // остановить проверку микрофона
        (PageHost.Content as DictionaryPage)?.SaveIfChanged(); // правки словаря — в модель, дальше их запишет Flush
        HotkeyCapture.CancelActive();
        IpcClient.Send(IpcProtocol.ResumeHotkey); // идемпотентно: если запись шла, демон не должен остаться без горячей клавиши
        // Ключи и настройки, введённые, но ещё не записанные (потеря фокуса не наступила). Окно закрывается — только журнал.
        if (!_model.Flush())
        {
            Log.Info("Окно настроек закрыто с несохранёнными изменениями (файл занят).");
        }
    }
}
