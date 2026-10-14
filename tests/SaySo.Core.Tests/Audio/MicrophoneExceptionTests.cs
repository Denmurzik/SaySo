using SaySo.Core.Audio.Wasapi;

namespace SaySo.Core.Tests.Audio;

public class MicrophoneExceptionTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070005), MicrophoneErrorKind.AccessDenied)]
    [InlineData(unchecked((int)0x80070490), MicrophoneErrorKind.NotFound)]
    [InlineData(unchecked((int)0x88890004), MicrophoneErrorKind.DeviceLost)]
    [InlineData(unchecked((int)0x8889000A), MicrophoneErrorKind.InUse)]
    [InlineData(unchecked((int)0x80004005), MicrophoneErrorKind.Other)]
    public void FromHResult_MapsKnownErrors(int hr, MicrophoneErrorKind kind)
    {
        var e = MicrophoneException.FromHResult(hr, "тест");
        Assert.Equal(kind, e.Kind);
        Assert.Equal(hr, e.NativeError);
        Assert.False(string.IsNullOrWhiteSpace(e.UserMessage));
    }

    [Fact]
    public void AccessDenied_ShortMessagePointsToSettings_DetailExplainsPrivacyPath()
    {
        var e = MicrophoneException.FromHResult(unchecked((int)0x80070005), "x");

        Assert.Contains("Параметры", e.UserMessage);
        Assert.Contains("Конфиденциальность", e.Detail);
    }
}
