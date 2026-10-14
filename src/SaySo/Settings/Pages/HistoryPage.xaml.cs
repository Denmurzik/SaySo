using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SaySo.Core.Diagnostics;
using SaySo.Core.Storage;
using Windows.ApplicationModel.DataTransfer;

namespace SaySo.Settings.Pages;

public sealed partial class HistoryPage : UserControl
{
    private readonly SettingsModel _model;

    internal HistoryPage(SettingsModel model)
    {
        _model = model;
        InitializeComponent();
        Render(model.History.Load());
    }

    private static string EngineName(string engine) => engine switch
    {
        "gemini" => "Gemini",
        "groq" => "Groq (резерв)",
        _ => engine,
    };

    private static void CopyToClipboard(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception e)
        {
            Log.Error("HistoryPage.Copy", e); // текст в журнал не пишем
        }
    }

    private void Render(IReadOnlyList<HistoryEntry> entries)
    {
        Items.Children.Clear();
        if (entries.Count == 0)
        {
            Items.Children.Add(new TextBlock { Text = "История пуста.", Style = (Style)Application.Current.Resources["CardDescription"] });
            return;
        }

        foreach (var entry in entries)
        {
            var copy = new Button { Content = "Копировать", VerticalAlignment = VerticalAlignment.Top };
            var text = entry.Text;
            copy.Click += (_, _) => CopyToClipboard(text);

            var body = new StackPanel { Spacing = 4 };
            body.Children.Add(new TextBlock
            {
                Text = $"{entry.At.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} · {EngineName(entry.Engine)}",
                Style = (Style)Application.Current.Resources["CardDescription"],
            });
            body.Children.Add(new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });

            var grid = new Grid { ColumnSpacing = 16 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(body);
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);

            Items.Children.Add(new Border { Style = (Style)Application.Current.Resources["SettingsCard"], Child = grid });
        }
    }

    private async void OnClear(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Очистить историю?",
                Content = "Все сохранённые диктовки будут удалены без возможности восстановления.",
                PrimaryButtonText = "Очистить",
                CloseButtonText = "Отмена",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            _model.History.Clear();
            Info.IsOpen = false;
            Render([]);
        }
        catch (Exception ex)
        {
            Log.Error("HistoryPage.Clear", ex);
            Info.Message = "Не удалось очистить историю: файл занят, попробуйте ещё раз.";
            Info.IsOpen = true;
        }
    }
}
