using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Core.Tests.Settings;

[Collection("Log")] // при сбоях Load пишет в статический Log
public class SoundThemeSettingsTests
{
    [Fact]
    public void NewSettings_UseFeltTheme() => Assert.Equal("5-voylok", new AppSettings().SoundTheme);

    [Theory]
    [InlineData("1-steklo", "1-steklo")]
    [InlineData("8-kalimba", "8-kalimba")]
    [InlineData("не такая тема", "5-voylok")]
    [InlineData("", "5-voylok")]
    [InlineData("   ", "5-voylok")]
    [InlineData(null, "5-voylok")]
    public void Normalize_UnknownOrEmptyTheme_BecomesDefault(string? input, string expected)
    {
        var s = new AppSettings { SoundTheme = input! };

        Assert.Equal(expected, SettingsNormalizer.Normalize(s).SoundTheme);
    }

    [Fact]
    public void SaveThenLoad_KeepsTheChosenTheme_AsCamelCaseKey()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new SettingsStore(paths);
        var s = store.Load();
        s.SoundTheme = "7-tap";
        store.Save(s);

        Assert.Contains("\"soundTheme\": \"7-tap\"", File.ReadAllText(paths.SettingsFile));
        Assert.Equal("7-tap", store.Load().SoundTheme);
    }

    [Fact]
    public void Load_OldFileWithoutTheme_GetsTheDefault()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.SettingsFile, """{ "soundsEnabled": false }""");

        var s = new SettingsStore(paths).Load();

        Assert.False(s.SoundsEnabled);
        Assert.Equal("5-voylok", s.SoundTheme);
    }

    [Theory]
    [InlineData("""{ "soundTheme": "9-nope" }""")]
    [InlineData("""{ "soundTheme": "" }""")]
    [InlineData("""{ "soundTheme": null }""")]
    public void Load_BrokenThemeInFile_BecomesDefault(string json)
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.SettingsFile, json);

        Assert.Equal("5-voylok", new SettingsStore(paths).Load().SoundTheme);
    }
}
