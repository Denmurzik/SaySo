using SaySo.Core.Audio.Wasapi;

namespace SaySo.Core.Tests.Audio;

public class MicrophoneSelectorTests
{
    private static CaptureEndpoint Active(string id, string name) => new(id, name, true);

    private static CaptureEndpoint Inactive(string id, string name) => new(id, name, false);

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("  ", "  ")]
    public void NothingSaved_NotRequested(string? id, string? name)
    {
        var r = MicrophoneSelector.Resolve(id, name, [Active("a", "Микрофон встроенный")]);

        Assert.Equal(MicrophoneMatch.NotRequested, r.Match);
        Assert.Null(r.Endpoint);
    }

    [Fact]
    public void IdMatchesActiveDevice_ById()
    {
        var r = MicrophoneSelector.Resolve("b", "Гарнитура", [Active("a", "Встроенный"), Active("b", "Гарнитура")]);

        Assert.Equal(MicrophoneMatch.ById, r.Match);
        Assert.Equal("b", r.Endpoint!.Id);
    }

    [Fact]
    public void IdMatch_WinsOverNameMatchOfAnotherDevice()
    {
        // Выбран встроенный микрофон при подключённой гарнитуре: имя гарнитуры в настройках не при чём.
        var r = MicrophoneSelector.Resolve("a", "Встроенный", [Active("b", "Встроенный"), Active("a", "Встроенный микрофон")]);

        Assert.Equal(MicrophoneMatch.ById, r.Match);
        Assert.Equal("a", r.Endpoint!.Id);
    }

    [Fact]
    public void IdInactive_ActiveDeviceWithSameName_ByName()
    {
        var r = MicrophoneSelector.Resolve("old", "Гарнитура", [Inactive("old", "Гарнитура"), Active("new", "Гарнитура"), Active("a", "Встроенный")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Equal("new", r.Endpoint!.Id);
    }

    [Fact]
    public void IdUnknown_NameMatchIsCaseInsensitive()
    {
        var r = MicrophoneSelector.Resolve("old", "гарнитура JABRA", [Active("new", "Гарнитура Jabra")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Equal("new", r.Endpoint!.Id);
    }

    [Fact]
    public void IdUnknown_NameWithSurroundingSpaces_ByName()
    {
        var r = MicrophoneSelector.Resolve("old", "  Гарнитура ", [Active("new", "Гарнитура")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
    }

    [Fact]
    public void IdUnknown_NoDeviceWithThatName_Fallback()
    {
        var r = MicrophoneSelector.Resolve("old", "Гарнитура", [Active("a", "Встроенный")]);

        Assert.Equal(MicrophoneMatch.Fallback, r.Match);
        Assert.Null(r.Endpoint);
    }

    [Fact]
    public void IdUnknown_NoName_Fallback()
    {
        var r = MicrophoneSelector.Resolve("old", null, [Active("a", "Встроенный")]);

        Assert.Equal(MicrophoneMatch.Fallback, r.Match);
        Assert.Null(r.Endpoint);
    }

    [Fact]
    public void SameNameOnlyOnInactiveDevice_Fallback()
    {
        var r = MicrophoneSelector.Resolve("old", "Гарнитура", [Inactive("old", "Гарнитура"), Inactive("other", "Гарнитура")]);

        Assert.Equal(MicrophoneMatch.Fallback, r.Match);
        Assert.Null(r.Endpoint);
    }

    [Fact]
    public void NoDevicesAtAll_Fallback()
    {
        var r = MicrophoneSelector.Resolve("old", "Гарнитура", []);

        Assert.Equal(MicrophoneMatch.Fallback, r.Match);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EmptySavedId_NameStillFindsDevice(string? id)
    {
        var r = MicrophoneSelector.Resolve(id, "Гарнитура", [Active("a", "Встроенный"), Active("new", "Гарнитура")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Equal("new", r.Endpoint!.Id);
    }

    [Fact]
    public void EmptySavedId_UnknownName_Fallback()
    {
        var r = MicrophoneSelector.Resolve("", "Гарнитура", [Active("a", "Встроенный")]);

        Assert.Equal(MicrophoneMatch.Fallback, r.Match);
    }

    [Fact]
    public void SeveralActiveWithSameName_PicksOneOfThem()
    {
        // Какое из одноимённых — не гарантируется (порядок перечисления Windows не определён), важно лишь найти одно из них.
        var r = MicrophoneSelector.Resolve("old", "Гарнитура", [Active("n1", "Гарнитура"), Active("n2", "Гарнитура")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Contains(r.Endpoint!.Id, new[] { "n1", "n2" });
    }

    [Theory]
    [InlineData("Микрофон (USB Audio)", "Микрофон (2- USB Audio)")]
    [InlineData("Микрофон (2- USB Audio)", "Микрофон (USB Audio)")]
    [InlineData("Микрофон (2- USB Audio)", "Микрофон (3- USB Audio)")]
    public void UsbPortNumberInName_IsIgnored(string saved, string connected)
    {
        var r = MicrophoneSelector.Resolve("old", saved, [Active("new", connected)]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Equal("new", r.Endpoint!.Id);
    }

    [Fact]
    public void ExactName_WinsOverNormalizedMatch_EvenWhenListedLater()
    {
        // Два одинаковых USB-устройства: «N- » — то, чем Windows их различает.
        var r = MicrophoneSelector.Resolve("old", "Микрофон (USB Audio)", [Active("n1", "Микрофон (2- USB Audio)"), Active("n2", "Микрофон (USB Audio)")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Equal("n2", r.Endpoint!.Id);
    }

    [Fact]
    public void ExactNameWithPortNumber_WinsOverNormalizedMatch_EvenWhenListedLater()
    {
        var r = MicrophoneSelector.Resolve("old", "Микрофон (2- USB Audio)", [Active("n1", "Микрофон (USB Audio)"), Active("n2", "Микрофон (2- USB Audio)")]);

        Assert.Equal("n2", r.Endpoint!.Id);
    }

    [Fact]
    public void NoExactName_FallsBackToNormalizedMatch()
    {
        var r = MicrophoneSelector.Resolve("old", "Микрофон (USB Audio)", [Active("n1", "Микрофон (Realtek Audio)"), Active("n2", "Микрофон (3- USB Audio)")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Equal("n2", r.Endpoint!.Id);
    }

    [Fact]
    public void MultiDigitPortNumber_IsIgnored()
    {
        var r = MicrophoneSelector.Resolve("old", "Микрофон (USB Audio)", [Active("new", "Микрофон (12- USB Audio)")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Equal("new", r.Endpoint!.Id);
        Assert.Equal("Микрофон (USB Audio)", MicrophoneSelector.NormalizeName("Микрофон (12- USB Audio)"));
    }

    [Fact]
    public void BluetoothHandsFreeProfiles_OfOneDevice_AreNotMerged()
    {
        // Одно физическое устройство Bluetooth в профиле «hands-free» даёт две конечные точки записи, различающиеся только
        // суффиксом профиля (HF / AG) — для Windows это разные устройства, и подменять одно другим нельзя.
        var r = MicrophoneSelector.Resolve("old", "Headset (Jabra Evolve Hands-Free HF Audio)", [Active("ag", "Headset (Jabra Evolve Hands-Free AG Audio)")]);

        Assert.Equal(MicrophoneMatch.Fallback, r.Match);
    }

    [Fact]
    public void BluetoothHandsFreeProfiles_ExactNameStillWins()
    {
        var r = MicrophoneSelector.Resolve("old", "Headset (Jabra Evolve Hands-Free HF Audio)",
            [Active("ag", "Headset (Jabra Evolve Hands-Free AG Audio)"), Active("hf", "Headset (Jabra Evolve Hands-Free HF Audio)")]);

        Assert.Equal(MicrophoneMatch.ByName, r.Match);
        Assert.Equal("hf", r.Endpoint!.Id);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("Микрофон", null)]       // заглушка: драйвер не отдал имя
    [InlineData("  Микрофон ", null)]
    [InlineData("Микрофон (USB Audio)", "Микрофон (USB Audio)")]
    [InlineData("  Гарнитура ", "Гарнитура")]
    public void NameToRemember_SkipsPlaceholderAndBlank(string? name, string? expected) =>
        Assert.Equal(expected, MicrophoneSelector.NameToRemember(name));

    [Fact]
    public void NameToRemember_PlaceholderConstantMatchesWhatDeviceListReturns() =>
        Assert.Null(MicrophoneSelector.NameToRemember(MicrophoneSelector.UnknownDeviceName));

    [Theory]
    [InlineData("Микрофон (Realtek(R) Audio)")]
    [InlineData("Микрофон (2 USB Audio)")]
    [InlineData("Headset (1-й вход)")]
    public void NormalizeName_LeavesOtherNamesAlone(string name) => Assert.Equal(name, MicrophoneSelector.NormalizeName(name));

    [Fact]
    public void UsbPortNumber_DoesNotMakeDifferentDevicesEqual()
    {
        var r = MicrophoneSelector.Resolve("old", "Микрофон (USB Audio)", [Active("a", "Микрофон (Realtek Audio)")]);

        Assert.Equal(MicrophoneMatch.Fallback, r.Match);
    }
}
