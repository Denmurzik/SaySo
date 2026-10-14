using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Diagnostics;

namespace SaySo.Settings.Pages;

public sealed partial class AboutPage : UserControl
{
    private readonly SettingsModel _model;

    internal AboutPage(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        VersionText.Text = $"Версия {typeof(AboutPage).Assembly.GetName().Version?.ToString(3)} · лицензия Apache-2.0";
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + _model.Paths.Root + "\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Error("AboutPage.OpenDataFolder", ex);
        }
    }
}
