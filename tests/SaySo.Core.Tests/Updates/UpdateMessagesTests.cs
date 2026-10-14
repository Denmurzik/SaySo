using SaySo.Core.Overlay;
using SaySo.Core.Updates;

namespace SaySo.Core.Tests.Updates;

public class UpdateMessagesTests
{
    [Fact]
    public void OverlayCatalogue_ListsEveryUpdateString_SoTheWidthTestMeasuresThem()
    {
        var all = OverlayMessages.All().ToHashSet();

        Assert.Contains(OverlayMessages.UpdateDownloading, all);
        Assert.Contains(OverlayMessages.UpdateCancelled, all);
        Assert.Contains(OverlayMessages.UpdateLaunchFailed, all);
        Assert.Contains(OverlayMessages.UpdateUpToDate, all);
        Assert.Contains(OverlayMessages.UpdateCheckFailed, all);
        foreach (var failure in Enum.GetValues<DownloadFailure>())
        {
            Assert.Contains(OverlayMessages.ForDownloadFailure(failure), all);
        }
    }

    [Fact]
    public void UpdateOverlayStrings_AreNotTrayErrorDetails() =>
        Assert.DoesNotContain(OverlayMessages.UpdateDownloading, OverlayMessages.AllDetails());

    [Fact]
    public void Balloon_FitsTheNotifyIconBuffers()
    {
        var widest = new Version(999, 999, 999);

        Assert.True(UpdateMessages.Balloon(widest).Length < 256); // szInfo
        Assert.True(UpdateMessages.BalloonTitle.Length < 64); // szInfoTitle
    }

    [Fact]
    public void Texts_UseThreeComponentVersion()
    {
        var version = new Version(0, 2, 0, 0);

        Assert.Equal("Доступна версия 0.2.0 — нажмите, чтобы обновить", UpdateMessages.Balloon(version));
        Assert.Equal("Обновить до 0.2.0", UpdateMessages.MenuItem(version));
        Assert.Equal("Доступна версия 0.2.0", UpdateMessages.Available(version));
        Assert.Equal("Установлена последняя версия", UpdateMessages.UpToDate);
    }

    [Fact]
    public void EveryCheckFailure_HasAReadableRussianText()
    {
        foreach (var failure in Enum.GetValues<UpdateFailure>())
        {
            Assert.Matches("[А-Яа-я]", UpdateMessages.CheckFailed(failure));
        }
    }
}
