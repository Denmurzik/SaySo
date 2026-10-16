using System.Globalization;
using SaySo.Core.Diagnostics;
using SaySo.Core.Storage;
using SaySo.Mac.Interop;
using SaySo.Mac.Platform;

namespace SaySo.Mac.Settings.Pages;

/// <summary>«История»: последние 20 диктовок с копированием и очисткой.</summary>
internal sealed class HistoryPage : IPage
{
    private const long AlertFirstButton = 1000; // NSAlertFirstButtonReturn

    private readonly SettingsModel _model;
    private readonly Ui _ui = new();
    private readonly nint _info;
    private readonly nint _items;

    public HistoryPage(SettingsModel model)
    {
        _model = model;
        var (scroll, column) = Ui.ScrollPage();
        View = scroll;
        _info = Ui.InfoLine();
        _items = Ui.Column(8);
        foreach (var view in new[]
        {
            Ui.Row(12, Ui.Title("История"), Ui.Spacer(), _ui.Button("Очистить историю", OnClear)),
            Ui.Label("Последние 20 диктовок. Хранятся только на этом компьютере в зашифрованном виде.", 12, secondary: true,
                wrapWidth: Ui.PageWidth),
            _info,
            _items,
        })
        {
            Ui.Append(column, view);
        }

        Render(model.History.Load());
    }

    public string Title => "История";

    public string Symbol => "clock.arrow.circlepath";

    public nint View { get; }

    public void OnLeave()
    {
    }

    public void Dispose() => _ui.Dispose();

    private static string EngineName(string engine) => engine switch
    {
        "gemini" => "Gemini",
        "groq" => "Groq (резерв)",
        _ => engine,
    };

    private void Render(IReadOnlyList<HistoryEntry> entries)
    {
        Ui.Clear(_items);
        if (entries.Count == 0)
        {
            Ui.Append(_items, Ui.Label("История пуста.", 12, secondary: true));
            return;
        }

        var textWidth = Ui.PageWidth - 2 * Ui.CardPadding - 130;
        foreach (var entry in entries)
        {
            var text = entry.Text;
            var body = Ui.Column(4,
                Ui.Label($"{entry.At.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)} · {EngineName(entry.Engine)}",
                    11, secondary: true),
                SelectableText(text, textWidth));
            var copy = _ui.Button("Копировать", () =>
            {
                if (!Pasteboard.SetText(text, transient: false))
                {
                    Log.Info("История: буфер обмена недоступен"); // текст в журнал не пишем
                }
            });
            Ui.Append(_items, Ui.Card(Ui.TopRow(16, body, Ui.Spacer(), copy)));
        }
    }

    private static nint SelectableText(string text, double width)
    {
        var label = Ui.Label(text, 13, wrapWidth: width);
        Ui.Selectable(label);
        return label;
    }

    private void OnClear()
    {
        var alert = ObjC.Send(ObjC.New("NSAlert"), ObjC.Sel("autorelease"));
        ObjC.SendVoid(alert, ObjC.Sel("setMessageText:"), ObjC.Str("Очистить историю?"));
        ObjC.SendVoid(alert, ObjC.Sel("setInformativeText:"), ObjC.Str("Все сохранённые диктовки будут удалены без возможности восстановления."));
        var clear = ObjC.Send(alert, ObjC.Sel("addButtonWithTitle:"), ObjC.Str("Очистить"));
        var cancel = ObjC.Send(alert, ObjC.Sel("addButtonWithTitle:"), ObjC.Str("Отмена"));
        ObjC.SendVoid(clear, ObjC.Sel("setKeyEquivalent:"), ObjC.Str(""));
        ObjC.SendVoid(cancel, ObjC.Sel("setKeyEquivalent:"), ObjC.Str("\r")); // по умолчанию — «Отмена», как в Windows
        if (ObjC.SendLong(alert, ObjC.Sel("runModal")) != AlertFirstButton)
        {
            return;
        }

        try
        {
            _model.History.Clear();
            Ui.Inform(_info, "", Severity.Informational);
            Render([]);
        }
        catch (Exception e)
        {
            Log.Error("HistoryPage.Clear", e);
            Ui.Inform(_info, "Не удалось очистить историю: файл занят, попробуйте ещё раз.", Severity.Error);
        }
    }
}
