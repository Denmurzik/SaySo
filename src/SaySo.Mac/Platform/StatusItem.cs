using SaySo.Core.Platform;
using SaySo.Mac.Interop;

namespace SaySo.Mac.Platform;

/// <summary>
/// Значок SaySo в строке меню (аналог значка в трее Windows): микрофон SF Symbols в цвете состояния, подсказка и меню,
/// которое собирается заново при каждом открытии (<paramref name="buildMenu"/>), как контекстное меню трея.
/// Только главный поток.
/// </summary>
internal sealed class StatusItem : IDisposable
{
    private const double VariableLength = -1; // NSVariableStatusItemLength

    private readonly Func<IReadOnlyList<TrayMenuItem>> _buildMenu;
    private readonly Action<uint> _onCommand;
    private readonly Bridge _menuDelegate;
    private readonly Bridge _target;
    private nint _item;
    private nint _menu;

    /// <param name="buildMenu">Пункты меню в момент открытия; перед этим вызывается <paramref name="onMenuOpening"/>.</param>
    public StatusItem(Func<IReadOnlyList<TrayMenuItem>> buildMenu, Action<uint> onCommand, Action onMenuOpening)
    {
        _buildMenu = buildMenu;
        _onCommand = onCommand;
        _target = new Bridge(sender => _onCommand((uint)ObjC.SendLong(sender, ObjC.Sel("tag"))));
        _menuDelegate = new Bridge
        {
            MenuNeedsUpdate = menu =>
            {
                onMenuOpening();
                Fill(menu);
            },
        };

        var bar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
        _item = ObjC.Retain(ObjC.Send(bar, ObjC.Sel("statusItemWithLength:"), VariableLength));
        _menu = ObjC.Send(ObjC.Alloc("NSMenu"), ObjC.Sel("initWithTitle:"), ObjC.Str("SaySo"));
        ObjC.SendVoid(_menu, ObjC.Sel("setAutoenablesItems:"), false);
        ObjC.SendVoid(_menu, ObjC.Sel("setDelegate:"), _menuDelegate.Handle);
        ObjC.SendVoid(_item, ObjC.Sel("setMenu:"), _menu);
    }

    /// <summary>Состояние значка и подсказка (как TrayIcon.Set в Windows).</summary>
    public void Set(TrayState state, string tooltip)
    {
        if (_item == 0)
        {
            return;
        }

        using var pool = AutoreleasePool.Push();
        var (symbol, tint) = state switch
        {
            TrayState.Recording => ("mic.fill", NS.Color("systemRedColor")),
            TrayState.Paused => ("mic.slash", (nint)0),
            TrayState.Error => ("mic.fill", NS.Color("systemOrangeColor")),
            _ => ("mic", (nint)0),
        };
        var button = ObjC.Send(_item, ObjC.Sel("button"));
        var image = NS.Symbol(symbol, "SaySo");
        if (image != 0)
        {
            ObjC.SendVoid(image, ObjC.Sel("setTemplate:"), true); // цвет строки меню (светлая/тёмная) подбирает система
            ObjC.SendVoid(button, ObjC.Sel("setImage:"), image);
        }
        else
        {
            ObjC.SendVoid(button, ObjC.Sel("setTitle:"), ObjC.Str("SaySo"));
        }

        ObjC.SendVoid(button, ObjC.Sel("setContentTintColor:"), tint);
        ObjC.SendVoid(button, ObjC.Sel("setToolTip:"), ObjC.Str(tooltip));
    }

    public void Dispose()
    {
        if (_item != 0)
        {
            var bar = ObjC.Send(ObjC.Class("NSStatusBar"), ObjC.Sel("systemStatusBar"));
            ObjC.SendVoid(bar, ObjC.Sel("removeStatusItem:"), _item);
            ObjC.Release(_item);
            _item = 0;
        }

        if (_menu != 0)
        {
            ObjC.SendVoid(_menu, ObjC.Sel("setDelegate:"), (nint)0);
            ObjC.Release(_menu);
            _menu = 0;
        }

        _menuDelegate.Dispose();
        _target.Dispose();
    }

    private void Fill(nint menu)
    {
        ObjC.SendVoid(menu, ObjC.Sel("removeAllItems"));
        foreach (var entry in _buildMenu())
        {
            if (entry.IsSeparator)
            {
                ObjC.SendVoid(menu, ObjC.Sel("addItem:"), ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("separatorItem")));
                continue;
            }

            var item = ObjC.Send(ObjC.Alloc("NSMenuItem"), ObjC.Sel("initWithTitle:action:keyEquivalent:"),
                ObjC.Str(entry.Text), Bridge.Action, ObjC.Str(""));
            ObjC.SendVoid(item, ObjC.Sel("setTarget:"), _target.Handle);
            ObjC.Send(item, ObjC.Sel("setTag:"), (nint)entry.Id);
            ObjC.SendVoid(item, ObjC.Sel("setEnabled:"), entry.Enabled);
            ObjC.Send(item, ObjC.Sel("setState:"), (nint)(entry.Checked ? 1 : 0));
            ObjC.SendVoid(menu, ObjC.Sel("addItem:"), item);
            ObjC.Release(item);
        }
    }
}
