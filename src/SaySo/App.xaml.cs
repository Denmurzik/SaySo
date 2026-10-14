using Microsoft.UI.Xaml;

namespace SaySo.Settings;

// App.xaml обязан лежать в корне проекта: из подпапки XamlControlsResources не находит ресурсы контролов
// (XamlParseException при любом TextBox/NavigationView, процесс падает с 0xC000027B).
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
