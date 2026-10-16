using SaySo.Mac.Interop;

namespace SaySo.Mac.Settings;

/// <summary>Важность сообщения на странице (как InfoBarSeverity в окне настроек Windows).</summary>
internal enum Severity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// Элементы управления AppKit для окна настроек. Возвращаемые представления — автоосвобождаемые: их удерживает родитель,
/// в который они добавлены. Обработчики (target/action, делегаты) живут в <see cref="Ui"/> экземпляре страницы и
/// освобождаются вместе с ней. Только главный поток.
/// </summary>
internal sealed class Ui : IDisposable
{
    public const double PageWidth = 600;
    public const double CardPadding = 14;

    private const long Horizontal = 0;
    private const long Vertical = 1;
    private const long AlignLeading = 5;   // NSLayoutAttributeLeading
    private const long AlignCenterY = 10;  // NSLayoutAttributeCenterY
    private const long AlignTop = 3;       // NSLayoutAttributeTop
    private const long ButtonTypeRadio = 4;
    private const long BoxCustom = 4;
    private const long NoTitle = 0;

    private readonly List<Bridge> _bridges = [];

    public void Dispose()
    {
        foreach (var bridge in _bridges)
        {
            bridge.Dispose();
        }

        _bridges.Clear();
    }

    /// <summary>Мост, который живёт столько же, сколько страница.</summary>
    public Bridge Keep(Bridge bridge)
    {
        _bridges.Add(bridge);
        return bridge;
    }

    // ---------- текст ----------

    public static nint Label(string text, double size = 13, bool bold = false, bool secondary = false, double wrapWidth = 0)
    {
        var label = wrapWidth > 0
            ? ObjC.Send(ObjC.Class("NSTextField"), ObjC.Sel("wrappingLabelWithString:"), ObjC.Str(text))
            : ObjC.Send(ObjC.Class("NSTextField"), ObjC.Sel("labelWithString:"), ObjC.Str(text));
        ObjC.SendVoid(label, ObjC.Sel("setFont:"), bold ? NS.Font(size, 0.23) : NS.Font(size));
        if (secondary)
        {
            ObjC.SendVoid(label, ObjC.Sel("setTextColor:"), NS.Color("secondaryLabelColor"));
        }

        if (wrapWidth > 0)
        {
            ObjC.SendVoid(label, ObjC.Sel("setPreferredMaxLayoutWidth:"), wrapWidth);
            Width(label, wrapWidth);
        }

        return label;
    }

    public static void SetText(nint field, string text) => ObjC.SendVoid(field, ObjC.Sel("setStringValue:"), ObjC.Str(text));

    public static string GetText(nint field) => ObjC.ToStr(ObjC.Send(field, ObjC.Sel("stringValue"))) ?? "";

    public static void Selectable(nint label) => ObjC.SendVoid(label, ObjC.Sel("setSelectable:"), true);

    /// <summary>Заголовок страницы.</summary>
    public static nint Title(string text) => Label(text, 22, bold: true);

    // ---------- кнопки и переключатели ----------

    public nint Button(string title, Action onClick)
    {
        var bridge = Keep(new Bridge(_ => onClick()));
        return ObjC.Send(ObjC.Class("NSButton"), ObjC.Sel("buttonWithTitle:target:action:"), ObjC.Str(title), bridge.Handle, Bridge.Action);
    }

    /// <summary>Кнопка по умолчанию (синяя, срабатывает по Enter).</summary>
    public nint AccentButton(string title, Action onClick)
    {
        var button = Button(title, onClick);
        ObjC.SendVoid(button, ObjC.Sel("setKeyEquivalent:"), ObjC.Str("\r"));
        return button;
    }

    public static void SetTitle(nint button, string title) => ObjC.SendVoid(button, ObjC.Sel("setTitle:"), ObjC.Str(title));

    public static void Enable(nint control, bool enabled) => ObjC.SendVoid(control, ObjC.Sel("setEnabled:"), enabled);

    public static void Show(nint view, bool visible) => ObjC.SendVoid(view, ObjC.Sel("setHidden:"), !visible);

    public nint Switch(bool on, Action<bool> onToggle)
    {
        var control = ObjC.Send(ObjC.New("NSSwitch"), ObjC.Sel("autorelease"));
        SetOn(control, on);
        var bridge = Keep(new Bridge(sender => onToggle(IsOn(sender))));
        ObjC.SendVoid(control, ObjC.Sel("setTarget:"), bridge.Handle);
        ObjC.SendVoid(control, ObjC.Sel("setAction:"), Bridge.Action);
        return control;
    }

    public static bool IsOn(nint control) => ObjC.SendLong(control, ObjC.Sel("state")) != 0;

    public static void SetOn(nint control, bool on) => ObjC.Send(control, ObjC.Sel("setState:"), (nint)(on ? 1 : 0));

    public nint Radio(string title, bool on, Action onSelect)
    {
        var button = Button(title, onSelect);
        ObjC.Send(button, ObjC.Sel("setButtonType:"), (nint)ButtonTypeRadio);
        SetOn(button, on);
        return button;
    }

    public nint PopUp(IEnumerable<string> items, int selected, Action<int> onChange)
    {
        var popup = ObjC.Send(ObjC.Send(ObjC.Alloc("NSPopUpButton"), ObjC.Sel("initWithFrame:pullsDown:"), new CGRect(0, 0, 220, 26), false),
            ObjC.Sel("autorelease"));
        foreach (var item in items)
        {
            AddItem(popup, item);
        }

        Select(popup, selected);
        var bridge = Keep(new Bridge(sender => onChange((int)ObjC.SendLong(sender, ObjC.Sel("indexOfSelectedItem")))));
        ObjC.SendVoid(popup, ObjC.Sel("setTarget:"), bridge.Handle);
        ObjC.SendVoid(popup, ObjC.Sel("setAction:"), Bridge.Action);
        return popup;
    }

    /// <summary>Добавляет пункт в конец (одинаковые названия NSPopUpButton не склеивает: пункт добавляется через меню).</summary>
    public static void AddItem(nint popup, string title)
    {
        var menu = ObjC.Send(popup, ObjC.Sel("menu"));
        var item = ObjC.Send(ObjC.Alloc("NSMenuItem"), ObjC.Sel("initWithTitle:action:keyEquivalent:"), ObjC.Str(title), (nint)0, ObjC.Str(""));
        ObjC.SendVoid(menu, ObjC.Sel("addItem:"), item);
        ObjC.Release(item);
    }

    public static void Select(nint popup, int index) => ObjC.SendVoid(popup, ObjC.Sel("selectItemAtIndex:"), (nint)index);

    public static int SelectedIndex(nint popup) => (int)ObjC.SendLong(popup, ObjC.Sel("indexOfSelectedItem"));

    public static int ItemCount(nint popup) => (int)ObjC.SendLong(popup, ObjC.Sel("numberOfItems"));

    public nint Slider(double min, double max, double value, double step, Action<double> onChange)
    {
        var bridge = Keep(new Bridge(sender => onChange(ObjC.SendDouble(sender, ObjC.Sel("doubleValue")))));
        var slider = ObjC.Send(ObjC.Class("NSSlider"), ObjC.Sel("sliderWithValue:minValue:maxValue:target:action:"), value, min, max,
            bridge.Handle, Bridge.Action);
        ObjC.Send(slider, ObjC.Sel("setNumberOfTickMarks:"), (nint)((max - min) / step + 1));
        ObjC.SendVoid(slider, ObjC.Sel("setAllowsTickMarkValuesOnly:"), true);
        Width(slider, 220);
        return slider;
    }

    // ---------- поля ввода ----------

    /// <param name="onChange">Каждое изменение текста.</param>
    /// <param name="onCommit">Конец редактирования (фокус ушёл, Enter, Tab).</param>
    public nint TextField(string text, string placeholder, Action<string>? onChange, Action<string>? onCommit, bool secure = false)
    {
        var field = ObjC.Send(ObjC.New(secure ? "NSSecureTextField" : "NSTextField"), ObjC.Sel("autorelease"));
        SetText(field, text);
        ObjC.SendVoid(field, ObjC.Sel("setPlaceholderString:"), ObjC.Str(placeholder));
        var bridge = Keep(new Bridge
        {
            TextDidChange = n => onChange?.Invoke(GetText(ObjC.Send(n, ObjC.Sel("object")))),
            TextDidEndEditing = n => onCommit?.Invoke(GetText(ObjC.Send(n, ObjC.Sel("object")))),
        });
        ObjC.SendVoid(field, ObjC.Sel("setDelegate:"), bridge.Handle);
        return field;
    }

    /// <summary>Многострочный текст с прокруткой (словарь): возвращает NSScrollView и сам NSTextView.</summary>
    public (nint Scroll, nint Text) TextArea(string text, double height, Action onChange)
    {
        var scroll = ObjC.Send(ObjC.Class("NSTextView"), ObjC.Sel("scrollableTextView"));
        var textView = ObjC.Send(scroll, ObjC.Sel("documentView"));
        ObjC.SendVoid(textView, ObjC.Sel("setFont:"), NS.Font(13));
        ObjC.SendVoid(textView, ObjC.Sel("setRichText:"), false);
        ObjC.SendVoid(textView, ObjC.Sel("setAutomaticQuoteSubstitutionEnabled:"), false);
        ObjC.SendVoid(textView, ObjC.Sel("setAutomaticDashSubstitutionEnabled:"), false);
        ObjC.SendVoid(textView, ObjC.Sel("setAutomaticSpellingCorrectionEnabled:"), false);
        ObjC.SendVoid(textView, ObjC.Sel("setString:"), ObjC.Str(text));
        var bridge = Keep(new Bridge { TextDidChange = _ => onChange() });
        ObjC.SendVoid(textView, ObjC.Sel("setDelegate:"), bridge.Handle);
        ObjC.Send(scroll, ObjC.Sel("setBorderType:"), (nint)2); // NSBezelBorder
        Width(scroll, PageWidth);
        Height(scroll, height);
        return (scroll, textView);
    }

    public static string TextOf(nint textView) => ObjC.ToStr(ObjC.Send(textView, ObjC.Sel("string"))) ?? "";

    public static void SetTextOf(nint textView, string text) => ObjC.SendVoid(textView, ObjC.Sel("setString:"), ObjC.Str(text));

    // ---------- раскладка ----------

    public static nint Column(double spacing, params nint[] views) => Stack(Vertical, spacing, AlignLeading, views);

    public static nint Row(double spacing, params nint[] views) => Stack(Horizontal, spacing, AlignCenterY, views);

    public static nint TopRow(double spacing, params nint[] views) => Stack(Horizontal, spacing, AlignTop, views);

    public static void Append(nint stack, nint view) => ObjC.SendVoid(stack, ObjC.Sel("addArrangedSubview:"), view);

    public static void Insert(nint stack, nint view, int index) =>
        ObjC.SendVoid(stack, ObjC.Sel("insertArrangedSubview:atIndex:"), view, (nint)index);

    public static void Remove(nint stack, nint view)
    {
        ObjC.SendVoid(stack, ObjC.Sel("removeArrangedSubview:"), view);
        ObjC.SendVoid(view, ObjC.Sel("removeFromSuperview"));
    }

    public static void Clear(nint stack)
    {
        var views = ObjC.Send(stack, ObjC.Sel("arrangedSubviews"));
        var list = new List<nint>();
        for (long i = 0; i < NS.Count(views); i++)
        {
            list.Add(NS.At(views, i));
        }

        foreach (var view in list)
        {
            Remove(stack, view);
        }
    }

    /// <summary>Гибкий промежуток в строке: прижимает следующие элементы к правому краю.</summary>
    public static nint Spacer()
    {
        var view = ObjC.Send(ObjC.New("NSView"), ObjC.Sel("autorelease"));
        ObjC.SendVoid(view, ObjC.Sel("setContentHuggingPriority:forOrientation:"), 1f, (nint)Horizontal);
        return view;
    }

    /// <summary>Карточка (как SettingsCard в Windows): скруглённая подложка с рамкой во всю ширину страницы.</summary>
    public static nint Card(nint content)
    {
        var box = ObjC.Send(ObjC.New("NSBox"), ObjC.Sel("autorelease"));
        ObjC.Send(box, ObjC.Sel("setBoxType:"), (nint)BoxCustom);
        ObjC.Send(box, ObjC.Sel("setTitlePosition:"), (nint)NoTitle);
        ObjC.SendVoid(box, ObjC.Sel("setCornerRadius:"), 8.0);
        ObjC.SendVoid(box, ObjC.Sel("setBorderWidth:"), 1.0);
        ObjC.SendVoid(box, ObjC.Sel("setBorderColor:"), NS.Color("separatorColor"));
        ObjC.SendVoid(box, ObjC.Sel("setFillColor:"), NS.Color("controlBackgroundColor"));
        ObjC.SendVoid(box, ObjC.Sel("setContentViewMargins:"), new CGSize(0, 0));
        Width(box, PageWidth);

        // Высоту карточки задаёт содержимое: оно прикреплено к contentView бокса ограничениями с полями.
        var inner = ObjC.Send(box, ObjC.Sel("contentView"));
        NoAutoresizing(content);
        ObjC.SendVoid(inner, ObjC.Sel("addSubview:"), content);
        Pin(content, "leadingAnchor", inner, "leadingAnchor", CardPadding);
        Pin(content, "trailingAnchor", inner, "trailingAnchor", -CardPadding);
        Pin(content, "topAnchor", inner, "topAnchor", CardPadding - 2);
        Pin(content, "bottomAnchor", inner, "bottomAnchor", -(CardPadding - 2));
        return box;
    }

    /// <summary>Карточка «заголовок и описание слева, элемент управления справа».</summary>
    public static nint SettingRow(string title, string description, nint control, double controlWidth)
    {
        var textWidth = PageWidth - 2 * CardPadding - controlWidth - 20;
        var text = Column(3, Label(title, 13, bold: true), Label(description, 11, secondary: true, wrapWidth: textWidth));
        return Card(Row(16, text, Spacer(), control));
    }

    /// <summary>Карточка «заголовок, описание и элементы под ними».</summary>
    public static nint SettingBlock(string title, string? description, params nint[] controls)
    {
        var views = new List<nint> { Label(title, 13, bold: true) };
        if (description is not null)
        {
            views.Add(Label(description, 11, secondary: true, wrapWidth: PageWidth - 2 * CardPadding));
        }

        views.AddRange(controls);
        return Card(Column(8, [.. views]));
    }

    public static void Width(nint view, double width) => Constrain(view, "widthAnchor", width);

    public static void Height(nint view, double height) => Constrain(view, "heightAnchor", height);

    /// <summary>Строка сообщения страницы (как InfoBar): цветной текст с переносом, скрыта, пока пуста.</summary>
    public static nint InfoLine()
    {
        var label = Label("", 12, wrapWidth: PageWidth);
        Selectable(label);
        Show(label, false);
        return label;
    }

    public static void Inform(nint line, string message, Severity severity)
    {
        SetText(line, message);
        ObjC.SendVoid(line, ObjC.Sel("setTextColor:"), NS.Color(severity switch
        {
            Severity.Error => "systemRedColor",
            Severity.Warning => "systemOrangeColor",
            Severity.Success => "systemGreenColor",
            _ => "secondaryLabelColor",
        }));
        Show(line, message.Length > 0);
    }

    /// <summary>Страница с прокруткой: столбец содержимого шириной <see cref="PageWidth"/> с полями.</summary>
    public static (nint Scroll, nint Column) ScrollPage()
    {
        var scroll = ObjC.Send(ObjC.New("NSScrollView"), ObjC.Sel("autorelease"));
        ObjC.SendVoid(scroll, ObjC.Sel("setHasVerticalScroller:"), true);
        ObjC.SendVoid(scroll, ObjC.Sel("setDrawsBackground:"), false);
        ObjC.SendVoid(scroll, ObjC.Sel("setAutohidesScrollers:"), true);
        var document = ObjC.Send(ObjC.Send(ObjC.Send(Overlay.Layers.FlippedViewClass, ObjC.Sel("alloc")), ObjC.Sel("init")), ObjC.Sel("autorelease"));
        NoAutoresizing(document);
        ObjC.SendVoid(scroll, ObjC.Sel("setDocumentView:"), document);
        var clip = ObjC.Send(scroll, ObjC.Sel("contentView"));
        Pin(document, "leadingAnchor", clip, "leadingAnchor", 0);
        Pin(document, "trailingAnchor", clip, "trailingAnchor", 0);
        Pin(document, "topAnchor", clip, "topAnchor", 0);

        var column = Column(10);
        NoAutoresizing(column);
        ObjC.SendVoid(document, ObjC.Sel("addSubview:"), column);
        Pin(column, "topAnchor", document, "topAnchor", 20);
        Pin(column, "bottomAnchor", document, "bottomAnchor", -24);
        Pin(column, "centerXAnchor", document, "centerXAnchor", 0);
        Width(column, PageWidth);
        return (scroll, column);
    }

    private static nint Stack(long orientation, double spacing, long alignment, nint[] views)
    {
        var stack = ObjC.Send(ObjC.Class("NSStackView"), ObjC.Sel("stackViewWithViews:"), NS.Array(views));
        ObjC.Send(stack, ObjC.Sel("setOrientation:"), (nint)orientation);
        ObjC.SendVoid(stack, ObjC.Sel("setSpacing:"), spacing);
        ObjC.Send(stack, ObjC.Sel("setAlignment:"), (nint)alignment);
        return stack;
    }

    private static void NoAutoresizing(nint view) =>
        ObjC.SendVoid(view, ObjC.Sel("setTranslatesAutoresizingMaskIntoConstraints:"), false);

    private static void Constrain(nint view, string anchor, double constant)
    {
        NoAutoresizing(view);
        var constraint = ObjC.Send(ObjC.Send(view, ObjC.Sel(anchor)), ObjC.Sel("constraintEqualToConstant:"), constant);
        ObjC.SendVoid(constraint, ObjC.Sel("setActive:"), true);
    }

    private static void Pin(nint view, string anchor, nint other, string otherAnchor, double constant)
    {
        var constraint = ObjC.Send(ObjC.Send(view, ObjC.Sel(anchor)), ObjC.Sel("constraintEqualToAnchor:constant:"),
            ObjC.Send(other, ObjC.Sel(otherAnchor)), constant);
        ObjC.SendVoid(constraint, ObjC.Sel("setActive:"), true);
    }
}
