using Microsoft.UI.Xaml;
using SaySo.Core.Diagnostics;

namespace SaySo.Settings;

// App.xaml обязан лежать в корне проекта: из подпапки XamlControlsResources не находит ресурсы контролов
// (XamlParseException при любом TextBox/NavigationView, процесс падает с 0xC000027B).
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        UnhandledException += OnUnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    // Необработанное исключение обработчика события не должно ронять окно настроек вместе с несохранёнными данными.
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Необработанное исключение окна настроек", e.Exception);
        e.Handled = e.Exception is not (OutOfMemoryException or AccessViolationException);
    }
}
