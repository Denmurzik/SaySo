using SaySo.Core.Interop;
using SaySo.Core.Overlay;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

[Trait("Category", "Integration")]
public class OverlayWindowIntegrationTests
{
    [Fact]
    public void ShowAllModes_DoesNotStealFocus_AndHides()
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var before = Native.GetForegroundWindow();
                using var overlay = new OverlayWindow(() => 0.7f);
                overlay.ShowRecording();
                Input.TestWindows.Pump(300);
                overlay.ShowProcessing();
                Input.TestWindows.Pump(300);
                overlay.ShowMessage("Проверка сообщения", OverlayTone.Error);
                Input.TestWindows.Pump(300);
                Assert.Equal(before, Native.GetForegroundWindow());
                overlay.Hide();
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
