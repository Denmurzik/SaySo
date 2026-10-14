using SaySo.Core.Interop;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;
using SaySo.Core.Tests.Input;

namespace SaySo.Core.Tests.Platform;

[Trait("Category", "Integration")]
public class OverlayWindowIntegrationTests
{
    // Повторное поднятие плашки идёт раз в 250 мс; запас на загруженную машину.
    private const int RaiseWaitMs = 700;

    [Fact]
    public void ShowAllModes_DoesNotStealFocus_AndHides() => OnStaThread(() =>
    {
        var before = Native.GetForegroundWindow();
        using var overlay = new OverlayWindow(() => 0.7f);
        overlay.ShowRecording();
        TestWindows.Pump(300);
        overlay.ShowProcessing();
        TestWindows.Pump(300);
        overlay.ShowMessage("Проверка сообщения", OverlayTone.Error);
        TestWindows.Pump(300);
        Assert.Equal(before, Native.GetForegroundWindow());
        overlay.Hide();
    });

    [Fact]
    public void ShownOverlay_IsInTopmostBand() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        overlay.ShowRecording();
        try
        {
            Assert.True(TestWindows.IsTopmost(overlay.Handle), "у плашки нет WS_EX_TOPMOST");
        }
        finally
        {
            overlay.Hide();
        }
    });

    [Fact]
    public void Recording_TopmostWindowCreatedLater_DoesNotStayAboveOverlay() => OnStaThread(() =>
    {
        var before = Native.GetForegroundWindow();
        using var overlay = new OverlayWindow(() => 0.7f);
        overlay.ShowRecording();
        TestWindows.Pump(100);

        var cover = TestWindows.CreateTopmostCover(0, 0, 300, 300);
        try
        {
            Assert.False(TestWindows.IsAbove(overlay.Handle, cover), "стенд: чужое topmost-окно должно сначала оказаться над плашкой");
            TestWindows.Pump(RaiseWaitMs);

            Assert.True(TestWindows.IsAbove(overlay.Handle, cover), "плашка осталась под чужим topmost-окном");
            Assert.Equal(before, Native.GetForegroundWindow()); // поднятие не берёт фокус
        }
        finally
        {
            TestWindows.Destroy(cover);
            overlay.Hide();
        }
    });

    [Fact]
    public void Message_TopmostWindowCreatedLater_DoesNotStayAboveOverlay() => OnStaThread(() =>
    {
        // Сообщение показывается без таймера анимации: поднимать плашку должен отдельный механизм.
        using var overlay = new OverlayWindow(() => 0);
        overlay.ShowMessage("Речь не распознана", OverlayTone.Info);
        TestWindows.Pump(100);

        var cover = TestWindows.CreateTopmostCover(0, 0, 300, 300);
        try
        {
            Assert.False(TestWindows.IsAbove(overlay.Handle, cover), "стенд: чужое topmost-окно должно сначала оказаться над плашкой");
            TestWindows.Pump(RaiseWaitMs);

            Assert.True(TestWindows.IsAbove(overlay.Handle, cover), "плашка осталась под чужим topmost-окном");
        }
        finally
        {
            TestWindows.Destroy(cover);
            overlay.Hide();
        }
    });

    [Fact]
    public void Show_AfterTopmostWindowAppeared_PutsOverlayAboveItImmediately() => OnStaThread(() =>
    {
        using var overlay = new OverlayWindow(() => 0);
        var cover = TestWindows.CreateTopmostCover(0, 0, 300, 300); // плашка создана раньше, окно-конкурент появилось позже
        try
        {
            overlay.ShowRecording();

            Assert.True(TestWindows.IsAbove(overlay.Handle, cover), "плашка показана под чужим topmost-окном");
        }
        finally
        {
            TestWindows.Destroy(cover);
            overlay.Hide();
        }
    });

    private static void OnStaThread(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null)
        {
            throw new Xunit.Sdk.XunitException(error.ToString());
        }
    }
}
