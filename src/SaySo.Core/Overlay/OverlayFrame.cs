namespace SaySo.Core.Overlay;

public enum OverlayMode
{
    Recording,
    Processing,
    Message,
}

public enum OverlayTone
{
    Neutral,
    Info,
    Error,
}

/// <summary>Маска покрытия текста (0..255), строки сверху вниз.</summary>
public sealed record TextMask(byte[] Alpha, int Width, int Height);

/// <param name="Levels">Уровни полосок 0..1 (используются первые <see cref="OverlayRenderer.BarCount"/>).</param>
/// <param name="TimeMs">Время анимации (для пульсации точек).</param>
public sealed record OverlayFrame(
    OverlayMode Mode,
    IReadOnlyList<float> Levels,
    long TimeMs,
    OverlayTone Tone = OverlayTone.Neutral,
    TextMask? Text = null);
