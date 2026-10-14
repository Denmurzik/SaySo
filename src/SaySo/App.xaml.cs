using Microsoft.UI.Xaml;
using SaySo.Core.Diagnostics;

namespace SaySo.Settings;

// App.xaml обязан лежать в корне проекта: из подпапки XamlControlsResources не находит ресурсы контролов
// (XamlParseException при любом TextBox/NavigationView, процесс падает с 0xC000027B).
public partial class App : Application
{
    private Window? _window;
    private bool _windowClosed;

    public App()
    {
        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = new MainWindow();
        window.Closed += (_, _) => _windowClosed = true;
        _window = window; // присваивается после конструктора: сбой при создании окна не должен считаться «окно живо»
        window.Activate();
    }

    // Исключение обработчика события не должно ронять живое окно вместе с несохранёнными данными. Если окна ещё нет
    // (сбой OnLaunched/конструктора) или оно закрыто — процесс без окна держал бы мьютекс настроек, и каждый следующий
    // запуск `--settings` молча завершался бы: тогда только журнал, и процесс падает.
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Необработанное исключение окна настроек", e.Exception);
        e.Handled = _window is not null && !_windowClosed;
    }
}
