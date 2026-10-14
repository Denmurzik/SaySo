using SaySo.Core.Settings;
using SaySo.Core.Storage;
using SaySo.Core.Updates;

namespace SaySo.Core.Tests.Updates;

[Collection("Log")] // при сбоях хранилище пишет в статический Log
public class UpdateStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Load_WhenFileMissing_ReturnsEmptyState()
    {
        using var dir = new TempDir();

        var state = new UpdateStateStore(new AppPaths(dir.Path)).Load();

        Assert.Null(state.LastCheckUtc);
        Assert.Null(state.DismissedVersion);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips_AndUsesCamelCaseNames()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new UpdateStateStore(paths);

        Assert.True(store.Save(new UpdateState { LastCheckUtc = Now, DismissedVersion = "0.2.0" }));

        var state = store.Load();
        Assert.Equal(Now, state.LastCheckUtc);
        Assert.Equal("0.2.0", state.DismissedVersion);
        var json = File.ReadAllText(paths.UpdateStateFile);
        Assert.Contains("\"lastCheckUtc\"", json);
        Assert.Contains("\"dismissedVersion\"", json);
        Assert.EndsWith("update-state.json", paths.UpdateStateFile);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"lastCheckUtc\": \"вчера\"}")]
    public void Load_CorruptedFile_ReturnsEmptyState(string content)
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.UpdateStateFile, content);

        var state = new UpdateStateStore(paths).Load();

        Assert.Null(state.LastCheckUtc);
        Assert.Null(state.DismissedVersion);
    }

    [Fact]
    public void Load_CorruptedFile_IsQuarantined()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.UpdateStateFile, "{ not json");

        new UpdateStateStore(paths).Load();

        Assert.True(File.Exists(paths.UpdateStateFile + ".bad"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("1.2")]
    [InlineData("1.2.3-beta")]
    public void Load_BadDismissedVersion_IsDropped(string dismissed)
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.UpdateStateFile, $$"""{"lastCheckUtc":"2026-10-14T12:00:00+00:00","dismissedVersion":"{{dismissed}}"}""");

        var state = new UpdateStateStore(paths).Load();

        Assert.Null(state.DismissedVersion);
        Assert.Equal(Now, state.LastCheckUtc);
    }

    [Fact]
    public void Save_FileLocked_ReturnsFalseInsteadOfThrowing()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new UpdateStateStore(paths);
        store.Save(new UpdateState());

        using (new HeldFile(paths.UpdateStateFile))
        {
            Assert.False(store.Save(new UpdateState { DismissedVersion = "0.2.0" }));
        }
    }

    [Fact]
    public void StateFile_DoesNotTouchSettings()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        new SettingsStore(paths).Save(new AppSettings());
        var before = File.ReadAllBytes(paths.SettingsFile);

        new UpdateStateStore(paths).Save(new UpdateState { LastCheckUtc = Now });

        Assert.Equal(before, File.ReadAllBytes(paths.SettingsFile));
    }
}

public class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NeverChecked_IsDue() => Assert.True(UpdatePolicy.IsCheckDue(new UpdateState(), Now));

    [Theory]
    [InlineData(0, false)]
    [InlineData(60, false)]
    [InlineData(24 * 60 - 1, false)]
    [InlineData(24 * 60, true)] // ровно 24 ч — уже пора
    [InlineData(24 * 60 + 1, true)]
    [InlineData(10 * 24 * 60, true)]
    public void IsDue_IsDecidedByTwentyFourHours(int minutesAgo, bool due)
    {
        var state = new UpdateState { LastCheckUtc = Now - TimeSpan.FromMinutes(minutesAgo) };

        Assert.Equal(due, UpdatePolicy.IsCheckDue(state, Now));
    }

    [Fact]
    public void LastCheckInTheFuture_ClockWentBack_IsDue() =>
        Assert.True(UpdatePolicy.IsCheckDue(new UpdateState { LastCheckUtc = Now + TimeSpan.FromDays(3) }, Now));

    [Fact]
    public void ShouldNotify_TrueUntilThatVersionIsDismissed()
    {
        var version = new Version(0, 2, 0);

        Assert.True(UpdatePolicy.ShouldNotify(new UpdateState(), version));
        Assert.False(UpdatePolicy.ShouldNotify(new UpdateState { DismissedVersion = "0.2.0" }, version));
        Assert.True(UpdatePolicy.ShouldNotify(new UpdateState { DismissedVersion = "0.2.0" }, new Version(0, 3, 0)));
        Assert.True(UpdatePolicy.ShouldNotify(new UpdateState { DismissedVersion = "0.1.9" }, version));
    }
}

public class CheckForUpdatesSettingTests
{
    [Fact]
    public void Default_IsOn() => Assert.True(new AppSettings().CheckForUpdates);

    [Fact]
    public void OldSettingsFile_WithoutTheField_StaysOn()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.SettingsFile, """{"schemaVersion":1,"soundsEnabled":false}""");

        var settings = new SettingsStore(paths).Load();

        Assert.True(settings.CheckForUpdates);
        Assert.False(settings.SoundsEnabled);
    }

    [Fact]
    public void TurnedOff_SurvivesSaveAndLoad()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(new AppPaths(dir.Path));
        var settings = store.Load();
        settings.CheckForUpdates = false;

        store.Save(settings);

        Assert.False(store.Load().CheckForUpdates);
    }
}
