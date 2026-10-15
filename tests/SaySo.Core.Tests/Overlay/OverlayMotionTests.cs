using System.Globalization;
using SaySo.Core.Overlay;

namespace SaySo.Core.Tests.Overlay;

public class OverlayMotionTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(0.2f)]
    [InlineData(OverlayMotion.MorphStart)]
    public void BeforeMorphStart_CapsuleIsACircle(float progress) =>
        Assert.Equal(36f, OverlayMotion.CapsuleWidth(progress, 36f, 112f), 3);

    [Theory]
    [InlineData(1f)]
    [InlineData(1.04f)] // перелёт пружины не раздувает капсулу шире финиша
    public void AtAndAfterFinish_CapsuleHasFullWidth(float progress) =>
        Assert.Equal(112f, OverlayMotion.CapsuleWidth(progress, 36f, 112f), 3);

    [Fact]
    public void Morph_IsMonotonic_AndHalfwayAtMiddleOfItsRange()
    {
        var previous = -1f;
        for (var p = 0f; p <= 1.001f; p += 0.01f)
        {
            var share = OverlayMotion.MorphShare(p);
            Assert.True(share >= previous, $"морфинг убывает при Progress {p}");
            previous = share;
        }

        Assert.Equal(0.5f, OverlayMotion.MorphShare((OverlayMotion.MorphStart + 1f) / 2f), 3);
    }

    [Fact]
    public void CapsuleY_IsNotClamped_SoSpringOvershootRisesAboveFinish()
    {
        Assert.Equal(100f, OverlayMotion.CapsuleY(0f, 100f, 8f));
        Assert.Equal(8f, OverlayMotion.CapsuleY(1f, 100f, 8f));
        Assert.True(OverlayMotion.CapsuleY(1.03f, 100f, 8f) < 8f);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.6f, 0f)]
    [InlineData(0.8f, 0.5f)]
    [InlineData(1f, 1f)]
    [InlineData(1.05f, 1f)]
    public void Content_AppearsBetween06And1(float progress, float opacity) =>
        Assert.Equal(opacity, OverlayMotion.ContentOpacity(progress), 3);

    [Fact]
    public void Expressions_ComputeTheSameAsCSharpFunctions()
    {
        for (var p = -0.1f; p <= 1.15f; p += 0.05f)
        {
            var vars = new Dictionary<string, float>
            {
                ["p.Progress"] = p,
                ["p.Width"] = 250f,
                ["p.Height"] = 36f,
                ["p.StartY"] = 120f,
                ["p.EndY"] = 8f,
            };

            Assert.Equal(OverlayMotion.CapsuleWidth(p, 36f, 250f), Eval(OverlayMotion.Expressions.Width, vars), 3);
            Assert.Equal(OverlayMotion.CapsuleY(p, 120f, 8f), Eval(OverlayMotion.Expressions.Y, vars), 3);
            Assert.Equal(OverlayMotion.ContentOpacity(p), Eval(OverlayMotion.Expressions.ContentOpacity, vars), 3);
        }
    }

    [Fact]
    public void ClipAndBorderExpressions_ComputeTheSameAsCSharpFunctions()
    {
        for (var p = -0.1f; p <= 1.15f; p += 0.05f)
        {
            var vars = new Dictionary<string, float>
            {
                ["p.Progress"] = p,
                ["p.Width"] = 250f,
                ["p.Height"] = 36f,
                ["p.MaxWidth"] = 480f,
            };
            var clip = OverlayMotion.ClipRect(p, 36f, 250f, 480f);
            var border = OverlayMotion.BorderRect(p, 36f, 250f, 480f);

            AssertVector((clip.Width, clip.Height), EvalVector2(OverlayMotion.Expressions.ClipSize, vars));
            AssertVector((clip.X, clip.Y), EvalVector2(OverlayMotion.Expressions.ClipOffset, vars));
            AssertVector((border.Width, border.Height), EvalVector2(OverlayMotion.Expressions.BorderSize, vars));
            AssertVector((border.X, border.Y), EvalVector2(OverlayMotion.Expressions.BorderOffset, vars));
        }
    }

    [Fact]
    public void ClipIsOnePixelInside_AndBorderSitsOnTheOuterPixel()
    {
        var clip = OverlayMotion.ClipRect(1f, 36f, 112f, 480f);
        var border = OverlayMotion.BorderRect(1f, 36f, 112f, 480f);

        Assert.Equal(new OverlayRect(185f, 1f, 110f, 34f), clip);
        Assert.Equal(new OverlayRect(184.5f, 0.5f, 111f, 35f), border); // обводка 1 px с центром на 0,5 px — ровно внешний пиксель
    }

    [Fact]
    public void FitText_KeepsEverythingThatFits_WithoutEllipsis() =>
        Assert.Equal((3, false), OverlayMotion.FitText([10f, 10f, 10f], [false, false, false], 8f, 30f));

    [Fact]
    public void FitText_CutsSoThatTextWithEllipsisFits()
    {
        float[] advances = [10f, 10f, 10f, 10f, 10f];
        var (keep, ellipsis) = OverlayMotion.FitText(advances, new bool[5], 8f, 35f);

        Assert.True(ellipsis);
        Assert.Equal(2, keep); // 10 + 10 + 8 ≤ 35, а 10·3 + 8 уже нет
    }

    [Fact]
    public void FitText_DropsSpacesBeforeEllipsis()
    {
        // «ab cd»: влезает «ab » + «…», пробел перед многоточием убирается.
        var (keep, ellipsis) = OverlayMotion.FitText([10f, 10f, 4f, 10f, 10f], [false, false, true, false, false], 8f, 33f);

        Assert.True(ellipsis);
        Assert.Equal(2, keep);
    }

    [Fact]
    public void FitText_WhenNothingFits_LeavesOnlyEllipsis() =>
        Assert.Equal((0, true), OverlayMotion.FitText([50f, 50f], [false, false], 8f, 5f));

    [Fact]
    public void Expressions_DoNotUseLerp_WhichClampsAndHidesOvershoot()
    {
        Assert.DoesNotContain("Lerp", OverlayMotion.Expressions.Width);
        Assert.DoesNotContain("Lerp", OverlayMotion.Expressions.Y);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    [InlineData(1.75f)]
    [InlineData(2f)]
    public void Layout_PutsCapsuleAboveWorkAreaBottom_AndWindowDownToMonitorBottom(float scale)
    {
        // Панель задач снизу: рабочая область кончается на 1032, монитор — на 1080.
        var layout = OverlayMotion.Layout(scale, monitorBottom: 1080, workLeft: 0, workRight: 1920, workBottom: 1032);

        var capsuleBottomOnScreen = layout.WindowTop + layout.EndY + layout.CapsuleHeight;
        Assert.Equal(1032 - OverlayMotion.Px(48, scale), capsuleBottomOnScreen);
        Assert.Equal(1080, layout.WindowTop + layout.WindowHeight);
        Assert.True(layout.StartY >= layout.WindowHeight, "до выезда капсула должна быть целиком за нижним краем экрана");
        Assert.Equal(OverlayMotion.Px(8, scale), layout.EndY); // запас на перелёт над финишем
        Assert.Equal(layout.CapsuleHeight & 1, layout.CapsuleMaxWidth & 1);
        Assert.InRange(layout.WindowLeft + layout.WindowWidth / 2, 959, 961);
        Assert.Equal(layout.WindowWidth, 2 * layout.CapsuleLeft + layout.CapsuleMaxWidth);
    }

    [Fact]
    public void Layout_RespectsWorkAreaOfALeftTaskbar()
    {
        var layout = OverlayMotion.Layout(1f, monitorBottom: 1080, workLeft: 48, workRight: 1920, workBottom: 1080);

        Assert.Equal(48 + (1872 - layout.WindowWidth) / 2, layout.WindowLeft);
        Assert.Equal(1080 - 48, layout.WindowTop + layout.EndY + layout.CapsuleHeight);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    public void WidthPx_HasParityOfTheBox_AndStaysBetweenCircleAndBox(float scale)
    {
        var layout = OverlayMotion.Layout(scale, 1080, 0, 1920, 1032);
        foreach (var dip in new[] { 0f, 20f, 112f, 113f, 250.5f, 479f, 480f, 900f })
        {
            var w = OverlayMotion.WidthPx(dip, layout);
            Assert.InRange(w, layout.CapsuleHeight, layout.CapsuleMaxWidth);
            Assert.Equal(0, (layout.CapsuleMaxWidth - w) % 2);
        }
    }

    [Fact]
    public void MessageWidth_GrowsWithText_AndIsCappedByTheBox()
    {
        var layout = OverlayMotion.Layout(1f, 1080, 0, 1920, 1032);

        Assert.InRange(OverlayMotion.MessageWidthPx(100, layout), 148, 149);
        Assert.Equal(layout.CapsuleMaxWidth, OverlayMotion.MessageWidthPx(5000, layout));
        Assert.True(OverlayMotion.MessageWidthPx(OverlayMotion.MaxTextWidth, layout) <= layout.CapsuleMaxWidth);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    public void Bars_AreCentered_FullyRoundable_AndFollowLevel(float scale)
    {
        var layout = OverlayMotion.Layout(scale, 1080, 0, 1920, 1032);

        var bars = OverlayMotion.Bars([0f, 0.5f, 1f, 2f, -1f, 0.25f, 0.75f], layout);

        Assert.Equal(OverlayMotion.BarCount, bars.Length);
        Assert.Equal(layout.CapsuleMaxWidth / 2f, (bars[0].X + bars[^1].X + bars[^1].Width) / 2f, 0);
        Assert.All(bars, b => Assert.Equal(OverlayMotion.Px(4, scale), b.Width));
        Assert.Equal(4f * scale, bars[0].Height, 3);
        Assert.Equal(20f * scale, bars[2].Height, 3);
        Assert.Equal(20f * scale, bars[3].Height, 3); // уровень выше 1 зажимается
        Assert.Equal(4f * scale, bars[4].Height, 3);  // и ниже 0 тоже
        Assert.All(bars, b => Assert.Equal(layout.CapsuleHeight / 2f, b.Y + b.Height / 2f, 3));
    }

    [Fact]
    public void Dots_AreSpacedBy12Dip_AroundTheCenter()
    {
        var layout = OverlayMotion.Layout(1.5f, 1080, 0, 1920, 1032);

        var dots = OverlayMotion.DotCenters(layout);

        Assert.Equal(layout.CapsuleMaxWidth / 2f, dots[1].X, 3);
        Assert.Equal(18f, dots[1].X - dots[0].X, 3);
        Assert.Equal(18f, dots[2].X - dots[1].X, 3);
    }

    [Fact]
    public void DotOpacity_PulsesBetween035And1()
    {
        for (var i = 0; i < 3; i++)
        {
            for (var t = 0f; t <= 1f; t += 0.05f)
            {
                Assert.InRange(OverlayMotion.DotOpacity(i, t), 0.35f - 1e-4f, 1f + 1e-4f);
            }
        }
    }

    [Fact]
    public void MessageParts_PlaceDotAndTextFromTheCapsuleLeftEdge()
    {
        var layout = OverlayMotion.Layout(1f, 1080, 0, 1920, 1032);
        var width = OverlayMotion.MessageWidthPx(200, layout);

        var parts = OverlayMotion.MessageParts(width, layout);

        var left = OverlayMotion.CapsuleLeft(width, layout);
        Assert.Equal(left + 16 + 4, parts.DotX, 3);
        Assert.Equal(4f, parts.DotRadius, 3);
        Assert.Equal(left + 32, parts.TextLeft, 3);
        Assert.Equal(layout.CapsuleHeight / 2f, parts.DotY, 3);
    }

    [Theory]
    [InlineData(22631, true, false, true, OverlayMaterial.Acrylic)]
    [InlineData(19045, true, false, true, OverlayMaterial.Solid)]  // Windows 10
    [InlineData(22631, false, false, true, OverlayMaterial.Solid)] // «Эффекты прозрачности» выключены
    [InlineData(22631, true, true, true, OverlayMaterial.Solid)]   // экономия заряда
    [InlineData(22631, true, false, false, OverlayMaterial.Solid)] // DWM не принял атрибут
    public void Material_FallsBackToSolid(int build, bool transparency, bool batterySaver, bool accepted, OverlayMaterial expected) =>
        Assert.Equal(expected, OverlayMotion.ChooseMaterial(build, transparency, batterySaver, accepted));

    private static void AssertVector((float X, float Y) expected, (float X, float Y) actual)
    {
        Assert.Equal(expected.X, actual.X, 3);
        Assert.Equal(expected.Y, actual.Y, 3);
    }

    /// <summary>«Vector2(a, b)»: компоненты вычисляются по отдельности.</summary>
    private static (float X, float Y) EvalVector2(string expression, IReadOnlyDictionary<string, float> vars)
    {
        const string Prefix = "Vector2(";
        Assert.StartsWith(Prefix, expression);
        Assert.EndsWith(")", expression);
        var inner = expression[Prefix.Length..^1];
        var depth = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            depth += inner[i] == '(' ? 1 : inner[i] == ')' ? -1 : 0;
            if (inner[i] == ',' && depth == 0)
            {
                return (Eval(inner[..i], vars), Eval(inner[(i + 1)..], vars));
            }
        }

        throw new Xunit.Sdk.XunitException("в Vector2 нет второй компоненты: " + expression);
    }

    // ---------- вычислитель строк ExpressionAnimation (числа, + − × /, скобки, Clamp, p.Свойство) ----------

    private static float Eval(string expression, IReadOnlyDictionary<string, float> vars)
    {
        var pos = 0;
        var value = Sum();
        Skip();
        Assert.True(pos == expression.Length, $"лишние символы в выражении с позиции {pos}: {expression}");
        return value;

        void Skip()
        {
            while (pos < expression.Length && expression[pos] == ' ')
            {
                pos++;
            }
        }

        bool Take(char c)
        {
            Skip();
            if (pos < expression.Length && expression[pos] == c)
            {
                pos++;
                return true;
            }

            return false;
        }

        float Sum()
        {
            var v = Product();
            while (true)
            {
                if (Take('+'))
                {
                    v += Product();
                }
                else if (Take('-'))
                {
                    v -= Product();
                }
                else
                {
                    return v;
                }
            }
        }

        float Product()
        {
            var v = Factor();
            while (true)
            {
                if (Take('*'))
                {
                    v *= Factor();
                }
                else if (Take('/'))
                {
                    v /= Factor();
                }
                else
                {
                    return v;
                }
            }
        }

        float Factor()
        {
            Skip();
            if (Take('-'))
            {
                return -Factor();
            }

            if (Take('('))
            {
                var inner = Sum();
                Assert.True(Take(')'), "нет закрывающей скобки: " + expression);
                return inner;
            }

            var start = pos;
            if (char.IsDigit(expression[pos]))
            {
                while (pos < expression.Length && (char.IsDigit(expression[pos]) || expression[pos] == '.'))
                {
                    pos++;
                }

                return float.Parse(expression[start..pos], CultureInfo.InvariantCulture);
            }

            while (pos < expression.Length && (char.IsLetterOrDigit(expression[pos]) || expression[pos] == '.'))
            {
                pos++;
            }

            var name = expression[start..pos];
            if (name == "Clamp")
            {
                Assert.True(Take('('));
                var x = Sum();
                Assert.True(Take(','));
                var lo = Sum();
                Assert.True(Take(','));
                var hi = Sum();
                Assert.True(Take(')'));
                return Math.Clamp(x, lo, hi);
            }

            Assert.True(vars.ContainsKey(name), $"неизвестное имя «{name}» в выражении: {expression}");
            return vars[name];
        }
    }
}
