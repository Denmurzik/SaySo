using System.Globalization;

namespace SaySo.Core.Overlay;

/// <summary>Материал капсулы: настоящий Acrylic (размытие подложки) или сплошной запасной фон.</summary>
public enum OverlayMaterial
{
    Acrylic,
    Solid,
}

/// <summary>
/// Раскладка окна плашки в физических пикселях. Окно неподвижно: от «финиш минус запас на перелёт» до нижнего края монитора;
/// капсула ездит внутри него (выезжает из-за нижнего края экрана).
/// </summary>
/// <param name="CapsuleLeft">X левого края «коробки» капсулы шириной <paramref name="CapsuleMaxWidth"/> внутри окна.</param>
/// <param name="EndY">Y верхнего края капсулы в покое (внутри окна).</param>
/// <param name="StartY">Y верхнего края капсулы до выезда: ниже окна, то есть за нижним краем экрана.</param>
public readonly record struct OverlayLayout(
    float Scale,
    int WindowLeft,
    int WindowTop,
    int WindowWidth,
    int WindowHeight,
    int CapsuleLeft,
    int CapsuleMaxWidth,
    int CapsuleHeight,
    int EndY,
    int StartY);

/// <summary>Прямоугольник внутри «коробки» капсулы (физические пиксели).</summary>
public readonly record struct OverlayRect(float X, float Y, float Width, float Height);

/// <summary>
/// Геометрия и движение плашки v2. Один параметр анимации Progress (0 → 1, пружина) задаёт положение по Y,
/// морфинг «круг → капсула» и проявление содержимого. Функции ниже — то же самое, что строки ExpressionAnimation
/// (<see cref="Expressions"/>), только в C#; модульный тест сверяет их между собой.
/// </summary>
public static class OverlayMotion
{
    public const float Height = 36f;
    public const float RecordingWidth = 112f;
    public const float MaxWidth = 480f;
    public const float BottomMargin = 48f;

    /// <summary>Запас над финишем: туда долетает пружина (перелёт 2–4 DIP).</summary>
    public const float OvershootRoom = 8f;

    public const float WindowSideMargin = 4f;

    /// <summary>До этого значения Progress капсула — круг (ширина = высота), дальше раздувается.</summary>
    public const float MorphStart = 0.35f;

    /// <summary>С этого значения Progress проявляется содержимое (столбики, точки, текст).</summary>
    public const float ContentStart = 0.6f;

    /// <summary>
    /// Period пружины компоновщика. У Windows.UI.Composition реальный период колебаний — 2π·Period (замер спайка),
    /// то есть 0,07 с — это отклик ~0,44 с: финиш через ~0,3 с, перелёт ~2 %, посадка к 0,45–0,55 с.
    /// </summary>
    public const float SpringPeriodSeconds = 0.07f;

    public const float SpringDampingRatio = 0.78f;

    public static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(280);

    /// <summary>Кривая скрытия: ease-in без перелёта, CubicBezier(0.4, 0, 1, 1).</summary>
    public static readonly (float X1, float Y1, float X2, float Y2) HideEasing = (0.4f, 0f, 1f, 1f);

    /// <summary>«Уменьшение движения» в Windows: вместо выезда и морфинга — растворение.</summary>
    public static readonly TimeSpan ReducedMotionFade = TimeSpan.FromMilliseconds(150);

    /// <summary>Смена содержимого между состояниями: старое гаснет, новое проявляется.</summary>
    public static readonly TimeSpan ContentFadeOut = TimeSpan.FromMilliseconds(120);

    public static readonly TimeSpan ContentFadeIn = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan ContentFadeInDelay = TimeSpan.FromMilliseconds(60);

    public const int BarCount = 7;
    public const float BarWidth = 4f;
    public const float BarGap = 4f;
    public const float BarMin = 4f;
    public const float BarMax = 20f;

    public const float DotRadius = 3f;
    public const float DotStep = 12f;
    public static readonly TimeSpan DotPeriod = TimeSpan.FromMilliseconds(900);

    public const float MessagePad = 16f;
    public const float ToneDotSize = 8f;
    public const float TextGap = 8f;

    /// <summary>Наибольшая ширина текста сообщения (DIP); длиннее — многоточие.</summary>
    public const float MaxTextWidth = 400f;

    public const float FontSize = 13f;

    // ---------- Progress → геометрия ----------

    /// <summary>Доля морфинга круг → капсула: smoothstep от (Progress − 0,35) / 0,65, зажатого в [0, 1].</summary>
    public static float MorphShare(float progress)
    {
        var t = Clamp01((progress - MorphStart) / (1f - MorphStart));
        return t * t * (3f - 2f * t);
    }

    /// <param name="height">Высота капсулы (ширина круга).</param>
    /// <param name="width">Ширина капсулы в покое.</param>
    public static float CapsuleWidth(float progress, float height, float width) => height + (width - height) * MorphShare(progress);

    /// <summary>Y без зажима: перелёт пружины (Progress &gt; 1) поднимает капсулу выше финиша — это и есть «инерция».</summary>
    public static float CapsuleY(float progress, float startY, float endY) => startY + (endY - startY) * progress;

    public static float ContentOpacity(float progress) => Clamp01((progress - ContentStart) / (1f - ContentStart));

    /// <summary>Строки ExpressionAnimation; параметр «p» — набор свойств с Progress, Width, Height, StartY, EndY.</summary>
    public static class Expressions
    {
        // Lerp компоновщика зажимает t в [0, 1] и съедает перелёт пружины, поэтому формулы записаны явно.
        private static readonly string Morph =
            $"Clamp((p.Progress - {F(MorphStart)}) / {F(1f - MorphStart)}, 0, 1)";

        private static readonly string Smooth = $"({Morph} * {Morph} * (3 - 2 * {Morph}))";

        public static readonly string Width = $"(p.Height + (p.Width - p.Height) * {Smooth})";

        public static readonly string Y = "(p.StartY + (p.EndY - p.StartY) * p.Progress)";

        public static readonly string ContentOpacity = $"Clamp((p.Progress - {F(ContentStart)}) / {F(1f - ContentStart)}, 0, 1)";

        private static string F(float v) => v.ToString("0.0###", CultureInfo.InvariantCulture);
    }

    // ---------- раскладка ----------

    /// <summary>Окно плашки внизу по центру рабочей области монитора; капсула в покое на 48 DIP выше её нижнего края.</summary>
    public static OverlayLayout Layout(float scale, int monitorBottom, int workLeft, int workRight, int workBottom)
    {
        var height = Px(Height, scale);
        var maxWidth = Px(MaxWidth, scale);
        if ((maxWidth & 1) != (height & 1))
        {
            maxWidth++; // ширина и высота одной чётности: в покое края капсулы на целых пикселях
        }

        var windowWidth = maxWidth + 2 * Px(WindowSideMargin, scale);
        var windowLeft = workLeft + (workRight - workLeft - windowWidth) / 2;
        var endY = Px(OvershootRoom, scale);
        var windowTop = workBottom - Px(BottomMargin, scale) - height - endY;
        var windowHeight = Math.Max(monitorBottom - windowTop, endY + height);
        return new OverlayLayout(scale, windowLeft, windowTop, windowWidth, windowHeight, (windowWidth - maxWidth) / 2, maxWidth, height,
            endY, windowHeight + 2);
    }

    /// <summary>
    /// Ширина капсулы в пикселях для ширины в DIP: не уже круга и не шире коробки, той же чётности, что коробка,
    /// чтобы центрированная капсула стояла на целых пикселях (рамка в 1 px остаётся резкой).
    /// </summary>
    public static int WidthPx(float dip, OverlayLayout layout)
    {
        var w = Math.Clamp(Px(dip, layout.Scale), layout.CapsuleHeight, layout.CapsuleMaxWidth);
        return (layout.CapsuleMaxWidth - w) % 2 == 0 ? w : w + 1;
    }

    /// <summary>Ширина капсулы сообщения: поля, точка тона, промежуток и текст.</summary>
    public static int MessageWidthPx(float textWidthPx, OverlayLayout layout) =>
        WidthPx(MessagePad + ToneDotSize + TextGap + MessagePad + textWidthPx / layout.Scale, layout);

    /// <summary>Левый край капсулы ширины <paramref name="widthPx"/> внутри коробки.</summary>
    public static float CapsuleLeft(int widthPx, OverlayLayout layout) => (layout.CapsuleMaxWidth - widthPx) / 2f;

    /// <summary>Столбики уровня по центру коробки; высота 4…20 DIP по уровню 0…1.</summary>
    public static OverlayRect[] Bars(ReadOnlySpan<float> levels, OverlayLayout layout)
    {
        var barWidth = Px(BarWidth, layout.Scale);
        var gap = Px(BarGap, layout.Scale);
        var total = BarCount * barWidth + (BarCount - 1) * gap;
        var x0 = (layout.CapsuleMaxWidth - total) / 2;
        var bars = new OverlayRect[BarCount];
        for (var i = 0; i < BarCount; i++)
        {
            var level = i < levels.Length ? Clamp01(levels[i]) : 0f;
            var h = (BarMin + level * (BarMax - BarMin)) * layout.Scale;
            bars[i] = new OverlayRect(x0 + i * (barWidth + gap), (layout.CapsuleHeight - h) / 2f, barWidth, h);
        }

        return bars;
    }

    /// <summary>Центры трёх точек обработки (радиус <see cref="DotRadius"/>·scale).</summary>
    public static (float X, float Y)[] DotCenters(OverlayLayout layout)
    {
        var centers = new (float X, float Y)[3];
        for (var i = 0; i < 3; i++)
        {
            centers[i] = (layout.CapsuleMaxWidth / 2f + (i - 1) * DotStep * layout.Scale, layout.CapsuleHeight / 2f);
        }

        return centers;
    }

    /// <summary>Яркость точки <paramref name="index"/> в доле периода <paramref name="phase"/> (0…1): волна бежит слева направо.</summary>
    public static float DotOpacity(int index, float phase) =>
        0.35f + 0.65f * (0.5f + 0.5f * MathF.Sin(phase * MathF.Tau - index * 0.9f));

    /// <summary>Центр точки тона и левый край текста сообщения в капсуле ширины <paramref name="widthPx"/>.</summary>
    public static (float DotX, float DotY, float DotRadius, float TextLeft) MessageParts(int widthPx, OverlayLayout layout)
    {
        var left = CapsuleLeft(widthPx, layout);
        var radius = Px(ToneDotSize, layout.Scale) / 2f;
        return (left + Px(MessagePad, layout.Scale) + radius, layout.CapsuleHeight / 2f, radius,
            left + Px(MessagePad + ToneDotSize + TextGap, layout.Scale));
    }

    /// <summary>
    /// Acrylic — только на Windows 11 (HostBackdropBrush для окон Win32), при включённых «Эффектах прозрачности»,
    /// без режима экономии заряда и если DWM принял DWMWA_USE_HOSTBACKDROPBRUSH. Иначе сплошной фон.
    /// </summary>
    public static OverlayMaterial ChooseMaterial(int osBuild, bool transparencyEffects, bool batterySaver, bool hostBackdropAccepted) =>
        osBuild >= 22000 && transparencyEffects && !batterySaver && hostBackdropAccepted ? OverlayMaterial.Acrylic : OverlayMaterial.Solid;

    public static int Px(float dip, float scale) => (int)MathF.Round(dip * scale);

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;
}
