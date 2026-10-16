using SaySo.Core.Diagnostics;
using SaySo.Core.Platform;
using SaySo.Mac.Interop;
using SaySo.Mac.Platform;
using SaySo.Mac.Settings.Pages;

namespace SaySo.Mac.Settings;

/// <summary>
/// Окно настроек — отдельный процесс <c>SaySo --settings</c> (как в Windows): закрытие окна завершает его, и демон не
/// держит в памяти AppKit-окна. Пока окно открыто, у SaySo есть значок в Dock. Второй запуск выводит открытое окно вперёд.
/// </summary>
internal static class SettingsApp
{
    private const string LockName = "settings";

    public static int Run()
    {
        var instance = InstanceLock.TryAcquire(LockName);
        if (instance is null)
        {
            var owner = InstanceLock.OwnerPid(LockName);
            if (owner > 0)
            {
                NS.Activate(owner);
            }

            return 0;
        }

        using (instance)
        {
            SettingsWindow? window = null;
            using var appDelegate = new Bridge
            {
                TerminateAfterLastWindowClosed = true,
                ShouldHandleReopen = () =>
                {
                    window?.Show();
                    return false;
                },
                // Cmd+Q и выход из учётной записи не закрывают окно (windowWillClose не придёт): записываем несохранённое здесь.
                ShouldTerminate = () =>
                {
                    window?.Close();
                    return 1; // NSTerminateNow
                },
            };
            AppHost.Initialize(NS.ActivationRegular, appDelegate);
            BuildMainMenu();
            using (window = new SettingsWindow(new SettingsModel()))
            {
                window.Show();
                AppHost.Run();
            }
        }

        return 0;
    }

    /// <summary>
    /// Главное меню: без меню «Правка» в полях ввода не работают Cmd+C, Cmd+V, Cmd+A (их обрабатывает первый ответчик
    /// через пункты меню), без меню приложения — Cmd+Q.
    /// </summary>
    private static void BuildMainMenu()
    {
        var main = NewMenu("");
        AddSubmenu(main, "SaySo",
            ("Скрыть SaySo", "hide:", "h"),
            (null, null, null),
            ("Закрыть окно", "performClose:", "w"),
            ("Завершить SaySo", "terminate:", "q"));
        AddSubmenu(main, "Правка",
            ("Отменить", "undo:", "z"),
            ("Повторить", "redo:", "Z"),
            (null, null, null),
            ("Вырезать", "cut:", "x"),
            ("Скопировать", "copy:", "c"),
            ("Вставить", "paste:", "v"),
            ("Выбрать все", "selectAll:", "a"));
        ObjC.SendVoid(NS.App, ObjC.Sel("setMainMenu:"), main);
        ObjC.Release(main);
    }

    private static nint NewMenu(string title) => ObjC.Send(ObjC.Alloc("NSMenu"), ObjC.Sel("initWithTitle:"), ObjC.Str(title));

    private static void AddSubmenu(nint main, string title, params (string? Title, string? Action, string? Key)[] items)
    {
        var menu = NewMenu(title);
        foreach (var (itemTitle, action, key) in items)
        {
            if (itemTitle is null)
            {
                ObjC.SendVoid(menu, ObjC.Sel("addItem:"), ObjC.Send(ObjC.Class("NSMenuItem"), ObjC.Sel("separatorItem")));
                continue;
            }

            // Без target: действие уходит по цепочке ответчиков (поле ввода, окно, приложение).
            var item = ObjC.Send(ObjC.Alloc("NSMenuItem"), ObjC.Sel("initWithTitle:action:keyEquivalent:"),
                ObjC.Str(itemTitle), ObjC.Sel(action!), ObjC.Str(key!));
            ObjC.SendVoid(menu, ObjC.Sel("addItem:"), item);
            ObjC.Release(item);
        }

        var holder = ObjC.Send(ObjC.Alloc("NSMenuItem"), ObjC.Sel("initWithTitle:action:keyEquivalent:"), ObjC.Str(title), (nint)0, ObjC.Str(""));
        ObjC.SendVoid(holder, ObjC.Sel("setSubmenu:"), menu);
        ObjC.SendVoid(main, ObjC.Sel("addItem:"), holder);
        ObjC.Release(holder);
        ObjC.Release(menu);
    }
}

/// <summary>Окно настроек: панель инструментов со страницами (стиль «Настройки» macOS) и текущая страница.</summary>
internal sealed class SettingsWindow : IDisposable
{
    private const long StyleTitled = 1, StyleClosable = 2, StyleMiniaturizable = 4, StyleResizable = 8;
    private const long ToolbarStylePreference = 2;
    private const double Width = Ui.PageWidth + 60;
    private const double Height = 640;

    private static readonly (string Id, Func<SettingsModel, IPage> Create)[] Pages =
    [
        ("general", m => new GeneralPage(m)),
        ("recognition", m => new RecognitionPage(m)),
        ("dictionary", m => new DictionaryPage(m)),
        ("microphone", m => new MicrophonePage(m)),
        ("history", m => new HistoryPage(m)),
        ("about", m => new AboutPage(m)),
    ];

    // Подписи и значки для панели инструментов (страница создаётся только при выборе).
    private static readonly Dictionary<string, (string Label, string Symbol)> Items = new()
    {
        ["general"] = ("Общие", "gearshape"),
        ["recognition"] = ("Распознавание", "waveform"),
        ["dictionary"] = ("Словарь", "character.book.closed"),
        ["microphone"] = ("Микрофон", "mic"),
        ["history"] = ("История", "clock.arrow.circlepath"),
        ["about"] = ("О программе", "info.circle"),
    };

    private readonly SettingsModel _model;
    private readonly Bridge _windowDelegate;
    private readonly Bridge _toolbarDelegate;
    private readonly Bridge _select;
    private readonly nint _window;
    private readonly nint _toolbar;
    private IPage? _page;
    private bool _closed;

    public SettingsWindow(SettingsModel model)
    {
        _model = model;
        _select = new Bridge(sender => Select(ObjC.ToStr(ObjC.Send(sender, ObjC.Sel("itemIdentifier"))) ?? "general"));
        _windowDelegate = new Bridge { WindowWillClose = OnClosing };
        _toolbarDelegate = new Bridge
        {
            ToolbarIdentifiers = () => NS.Array(Pages.Select(p => ObjC.Str(p.Id)).ToArray()),
            ToolbarItem = CreateToolbarItem,
        };

        _window = ObjC.Send(ObjC.Alloc("NSWindow"), ObjC.Sel("initWithContentRect:styleMask:backing:defer:"),
            new CGRect(0, 0, Width, Height), (nint)(StyleTitled | StyleClosable | StyleMiniaturizable | StyleResizable), (nint)2, false);
        ObjC.SendVoid(_window, ObjC.Sel("setReleasedWhenClosed:"), false);
        ObjC.SendVoid(_window, ObjC.Sel("setTitle:"), ObjC.Str("SaySo"));
        ObjC.SendVoid(_window, ObjC.Sel("setContentMinSize:"), new CGSize(Width, 420));
        ObjC.SendVoid(_window, ObjC.Sel("setContentMaxSize:"), new CGSize(Width, 4000));
        ObjC.SendVoid(_window, ObjC.Sel("setDelegate:"), _windowDelegate.Handle);
        ObjC.SendVoid(_window, ObjC.Sel("setFrameAutosaveName:"), ObjC.Str("SaySo.Settings"));

        _toolbar = ObjC.Send(ObjC.Alloc("NSToolbar"), ObjC.Sel("initWithIdentifier:"), ObjC.Str("SaySo.Settings.Toolbar"));
        ObjC.SendVoid(_toolbar, ObjC.Sel("setDelegate:"), _toolbarDelegate.Handle);
        ObjC.SendVoid(_toolbar, ObjC.Sel("setAllowsUserCustomization:"), false);
        ObjC.Send(_toolbar, ObjC.Sel("setDisplayMode:"), (nint)1); // NSToolbarDisplayModeIconAndLabel
        ObjC.SendVoid(_window, ObjC.Sel("setToolbar:"), _toolbar);
        ObjC.Send(_window, ObjC.Sel("setToolbarStyle:"), (nint)ToolbarStylePreference);
        // Только для разработки и снимков экрана: SAYSO_SETTINGS_PAGE=recognition|dictionary|… открывает эту страницу.
        var start = Environment.GetEnvironmentVariable("SAYSO_SETTINGS_PAGE");
        Select(Pages.Any(p => p.Id == start) ? start! : "general");
        ObjC.SendVoid(_window, ObjC.Sel("center"));
    }

    public void Show()
    {
        ObjC.SendVoid(_window, ObjC.Sel("makeKeyAndOrderFront:"), (nint)0);
        NS.ActivateApp();
    }

    /// <summary>Шаги закрытия окна (запись несохранённого, возобновление горячей клавиши у демона); идемпотентно.</summary>
    public void Close() => OnClosing();

    public void Dispose()
    {
        OnClosing();
        _page?.Dispose();
        _page = null;
        ObjC.SendVoid(_window, ObjC.Sel("setDelegate:"), (nint)0);
        ObjC.SendVoid(_toolbar, ObjC.Sel("setDelegate:"), (nint)0);
        ObjC.Release(_toolbar);
        ObjC.Release(_window);
        _windowDelegate.Dispose();
        _toolbarDelegate.Dispose();
        _select.Dispose();
    }

    private nint CreateToolbarItem(string id)
    {
        if (!Items.TryGetValue(id, out var info))
        {
            return 0;
        }

        var item = ObjC.Send(ObjC.Send(ObjC.Alloc("NSToolbarItem"), ObjC.Sel("initWithItemIdentifier:"), ObjC.Str(id)), ObjC.Sel("autorelease"));
        ObjC.SendVoid(item, ObjC.Sel("setLabel:"), ObjC.Str(info.Label));
        ObjC.SendVoid(item, ObjC.Sel("setImage:"), NS.Symbol(info.Symbol, info.Label));
        ObjC.SendVoid(item, ObjC.Sel("setTarget:"), _select.Handle);
        ObjC.SendVoid(item, ObjC.Sel("setAction:"), Bridge.Action);
        return item;
    }

    private void Select(string id)
    {
        if (_page is not null)
        {
            // Правки страницы попадают в модель до Flush; если файл занят, предупреждение — в журнал и на новой странице.
            _page.OnLeave();
        }

        var saved = _model.Flush();
        var old = _page;
        var factory = Pages.FirstOrDefault(p => p.Id == id).Create ?? Pages[0].Create;
        _page = factory(_model);
        ObjC.SendVoid(_window, ObjC.Sel("setContentView:"), _page.View);
        ObjC.SendVoid(_toolbar, ObjC.Sel("setSelectedItemIdentifier:"), ObjC.Str(id));
        ObjC.SendVoid(_window, ObjC.Sel("setTitle:"), ObjC.Str(_page.Title));
        old?.Dispose();
        if (!saved)
        {
            Log.Info("Окно настроек: не все изменения записаны (файл занят)");
        }
    }

    // Завершение окна: шаги страницы (проверка микрофона, словарь, запись клавиши), затем запись несохранённого.
    private void OnClosing()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _page?.OnLeave();
        HotkeyCapture.CancelActive();
        Ipc.Send(IpcProtocol.ResumeHotkey); // идемпотентно: если запись шла, демон не должен остаться без горячей клавиши
        if (!_model.Flush())
        {
            Log.Info("Окно настроек закрыто с несохранёнными изменениями (файл занят).");
        }
    }
}
