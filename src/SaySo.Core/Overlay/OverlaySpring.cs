namespace SaySo.Core.Overlay;

/// <summary>
/// Движение плашки macOS по времени (кадры считает сама плашка) — те же параметры, что у пружины компоновщика Windows (<see cref="OverlayMotion"/>):
/// собственная частота 1/Period (реальный период колебаний 2π·Period), затухание 0,78 — финиш около 0,3 с, перелёт ~2 %,
/// посадка к 0,45–0,55 с. Скрытие — кривая CubicBezier(0,4; 0; 1; 1) за 280 мс.
/// </summary>
public static class OverlaySpring
{
    /// <summary>Дольше этого пружина считается успокоившейся (остаток меньше 0,1 %).</summary>
    public const double SettleSeconds = 0.8;

    /// <summary>
    /// Progress пружины через <paramref name="seconds"/> после старта из <paramref name="from"/> с нулевой скоростью к 1.
    /// Может быть больше 1 (перелёт): капсула при этом поднимается выше финиша.
    /// </summary>
    public static double Reveal(double from, double seconds)
    {
        if (seconds <= 0)
        {
            return from;
        }

        if (seconds >= SettleSeconds)
        {
            return 1;
        }

        var omega = 1.0 / OverlayMotion.SpringPeriodSeconds;
        var zeta = (double)OverlayMotion.SpringDampingRatio;
        var damped = omega * Math.Sqrt(1 - zeta * zeta);
        var decay = Math.Exp(-zeta * omega * seconds);
        return 1 - (1 - from) * decay * (Math.Cos(damped * seconds) + zeta / Math.Sqrt(1 - zeta * zeta) * Math.Sin(damped * seconds));
    }

    /// <summary>Progress скрытия: от <paramref name="from"/> к 0 по кривой ease-in за <see cref="OverlayMotion.HideDuration"/>.</summary>
    public static double Hide(double from, double seconds)
    {
        var t = seconds / OverlayMotion.HideDuration.TotalSeconds;
        return t >= 1 ? 0 : from * (1 - CubicBezier(OverlayMotion.HideEasing, Math.Max(0, t)));
    }

    /// <summary>Значение кривой Безье (0,0)–(x1,y1)–(x2,y2)–(1,1) для доли времени <paramref name="x"/>.</summary>
    public static double CubicBezier((float X1, float Y1, float X2, float Y2) curve, double x)
    {
        // Параметр t для заданного x — методом Ньютона с запасным делением пополам.
        double X(double t) => Bezier(curve.X1, curve.X2, t);
        var lo = 0.0;
        var hi = 1.0;
        var tt = x;
        for (var i = 0; i < 8; i++)
        {
            var err = X(tt) - x;
            if (Math.Abs(err) < 1e-6)
            {
                return Bezier(curve.Y1, curve.Y2, tt);
            }

            var d = Derivative(curve.X1, curve.X2, tt);
            if (Math.Abs(d) < 1e-6)
            {
                break;
            }

            tt -= err / d;
        }

        tt = x;
        for (var i = 0; i < 30; i++)
        {
            var v = X(tt);
            if (Math.Abs(v - x) < 1e-6)
            {
                break;
            }

            if (v < x)
            {
                lo = tt;
            }
            else
            {
                hi = tt;
            }

            tt = (lo + hi) / 2;
        }

        return Bezier(curve.Y1, curve.Y2, tt);
    }

    /// <summary>Плавное смещение 0 → 1 (ease-out) для смены ширины и растворения содержимого.</summary>
    public static double EaseOut(double t) => t >= 1 ? 1 : t <= 0 ? 0 : 1 - Math.Pow(1 - t, 3);

    private static double Bezier(double p1, double p2, double t)
    {
        var u = 1 - t;
        return 3 * u * u * t * p1 + 3 * u * t * t * p2 + t * t * t;
    }

    private static double Derivative(double p1, double p2, double t)
    {
        var u = 1 - t;
        return 3 * u * u * p1 + 6 * u * t * (p2 - p1) + 3 * t * t * (1 - p2);
    }
}
