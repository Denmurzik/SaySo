using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Interop;
using SaySo.Settings.Pages;

namespace SaySo.Settings;

public sealed partial class MainWindow : Window
{
    private readonly SettingsModel _model = new();

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = Native.GetDpiForWindow(hwnd) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(980 * scale), (int)(720 * scale)));
        Closed += (_, _) => _model.SaveSecrets();
        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void OnNavigationChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        _model.SaveSecrets();
        PageHost.Content = tag switch
        {
            "general" => new GeneralPage(_model),
            "recognition" => new RecognitionPage(_model),
            _ => new TextBlock { Text = "Раздел появится в следующей задаче.", Margin = new Thickness(32) }, // задача 18
        };
    }
}
