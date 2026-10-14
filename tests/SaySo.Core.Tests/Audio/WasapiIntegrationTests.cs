using SaySo.Core.Audio;
using SaySo.Core.Audio.Wasapi;

namespace SaySo.Core.Tests.Audio;

/// <summary>Требуют микрофон и разрешение доступа классическим приложениям. На ~1 с включают индикатор микрофона.</summary>
[Trait("Category", "Integration")]
public class WasapiIntegrationTests
{
    [Fact]
    public void ListCaptureDevices_ReturnsNamedDevices()
    {
        var devices = WasapiDevices.ListCaptureDevices();
        Assert.All(devices, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Id));
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
        });
    }

    [Fact]
    public async Task Capture_DefaultDevice_DeliversPcm()
    {
        Assert.SkipWhen(WasapiDevices.ListCaptureDevices().Count == 0, "Нет микрофона.");
        long total = 0;
        var oddChunk = false;
        using var capture = new WasapiCapture(null, data =>
        {
            Interlocked.Add(ref total, data.Length);
            oddChunk |= data.Length % 2 != 0;
        });
        await capture.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(700);
        capture.Stop();

        Assert.False(oddChunk);
        Assert.InRange(Interlocked.Read(ref total), AudioFormat.BytesPerSecond * 3 / 10, AudioFormat.BytesPerSecond * 2);
    }

    [Fact]
    public async Task Capture_UnknownDeviceId_FallsBackToDefault()
    {
        Assert.SkipWhen(WasapiDevices.ListCaptureDevices().Count == 0, "Нет микрофона.");
        long total = 0;
        using var capture = new WasapiCapture("{нет-такого-устройства}", data => Interlocked.Add(ref total, data.Length));
        await capture.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(400);
        capture.Stop();
        Assert.True(Interlocked.Read(ref total) > 0);
    }

    [Fact]
    public async Task Stop_IsIdempotent_AndStartTwiceThrows()
    {
        Assert.SkipWhen(WasapiDevices.ListCaptureDevices().Count == 0, "Нет микрофона.");
        var capture = new WasapiCapture(null, _ => { });
        await capture.StartAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Throws<InvalidOperationException>(() => { _ = capture.StartAsync(); });
        capture.Stop();
        capture.Stop();
        capture.Dispose();
    }
}
