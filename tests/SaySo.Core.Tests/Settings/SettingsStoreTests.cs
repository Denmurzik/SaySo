using SaySo.Core.Settings;
using SaySo.Core.Storage;

namespace SaySo.Core.Tests.Settings;

[Collection("Log")] // при сбоях Load пишет в статический Log
public class SettingsStoreTests
{
    [Fact]
    public void Load_FileLockedForGood_ReturnsDefaultsWithoutThrowing()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new SettingsStore(paths);
        var s = store.Load();
        s.Hotkey.TapThresholdMs = 400;
        store.Save(s);

        using (new HeldFile(paths.SettingsFile))
        {
            Assert.Equal(300, store.Load().Hotkey.TapThresholdMs);
        }

        Assert.False(File.Exists(paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void Load_FileBrieflyLocked_RetriesAndReturnsContent()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new SettingsStore(paths);
        var s = store.Load();
        s.Hotkey.TapThresholdMs = 400;
        store.Save(s);

        using (new HeldFile(paths.SettingsFile).ReleaseAfter(TimeSpan.FromMilliseconds(80)))
        {
            Assert.Equal(400, store.Load().Hotkey.TapThresholdMs);
        }
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        using var dir = new TempDir();
        var s = new SettingsStore(new AppPaths(dir.Path)).Load();

        Assert.Equal(HotkeySettings.DefaultKeys(), s.Hotkey.Keys);
        Assert.Equal(300, s.Hotkey.TapThresholdMs);
        Assert.Equal("gemini-3.5-transcribe-live", s.Recognition.GeminiModel);
        Assert.Equal(TranscriptionMode.Smart, s.Recognition.Mode);
        Assert.Empty(s.Recognition.LanguageCodes);
        Assert.True(s.Recognition.GroqFallbackEnabled);
        Assert.True(s.Recognition.GroqCleanupEnabled);
        Assert.Equal("whisper-large-v3", s.Recognition.GroqWhisperModel);
        Assert.Equal("openai/gpt-oss-120b", s.Recognition.GroqCleanupModel);
        Assert.True(s.SoundsEnabled);
        Assert.True(s.AutostartEnabled);
        Assert.False(s.RunAsAdmin);
        Assert.Null(s.MicrophoneDeviceId);
        Assert.Null(s.MicrophoneDeviceName);
        Assert.Empty(s.Vocabulary);
    }

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(new AppPaths(dir.Path));
        var s = store.Load();
        s.Hotkey.Keys = [0xA2, 0x5B];
        s.Hotkey.TapThresholdMs = 400;
        s.Recognition.Mode = TranscriptionMode.Verbatim;
        s.Recognition.LanguageCodes = ["ru-RU"];
        s.Vocabulary = ["Kubernetes", "SaySo"];
        s.MicrophoneDeviceId = "{0.0.1.00000000}.{abc}";
        s.MicrophoneDeviceName = "Гарнитура (USB Audio)";
        s.SoundsEnabled = false;
        store.Save(s);

        var loaded = store.Load();
        Assert.Equal(new[] { 0xA2, 0x5B }, loaded.Hotkey.Keys);
        Assert.Equal(400, loaded.Hotkey.TapThresholdMs);
        Assert.Equal(TranscriptionMode.Verbatim, loaded.Recognition.Mode);
        Assert.Equal(new[] { "ru-RU" }, loaded.Recognition.LanguageCodes);
        Assert.Equal(new[] { "Kubernetes", "SaySo" }, loaded.Vocabulary);
        Assert.Equal("{0.0.1.00000000}.{abc}", loaded.MicrophoneDeviceId);
        Assert.Equal("Гарнитура (USB Audio)", loaded.MicrophoneDeviceName);
        Assert.False(loaded.SoundsEnabled);
    }

    [Fact]
    public void Save_WritesEnumAsString()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new SettingsStore(paths);
        store.Save(store.Load());
        Assert.Contains("Smart", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsDefaultsAndKeepsBackup()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.SettingsFile, "{ это не json");

        var s = new SettingsStore(paths).Load();

        Assert.Equal(300, s.Hotkey.TapThresholdMs);
        Assert.True(File.Exists(paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void Load_NullSections_AreReplacedWithDefaults()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.SettingsFile, """{ "hotkey": null, "recognition": null, "vocabulary": null }""");

        var s = new SettingsStore(paths).Load();

        Assert.Equal(HotkeySettings.DefaultKeys(), s.Hotkey.Keys);
        Assert.Equal("gemini-3.5-transcribe-live", s.Recognition.GeminiModel);
        Assert.Empty(s.Vocabulary);
    }

    [Theory]
    [InlineData(10, 150)]
    [InlineData(300, 300)]
    [InlineData(5000, 1000)]
    public void Normalize_ClampsTapThreshold(int input, int expected)
    {
        var s = new AppSettings();
        s.Hotkey.TapThresholdMs = input;
        Assert.Equal(expected, SettingsNormalizer.Normalize(s).Hotkey.TapThresholdMs);
    }

    [Fact]
    public void DefaultHotkey_IsRightCtrl_OnMacRightCmd() =>
        Assert.Equal(OperatingSystem.IsMacOS() ? new[] { 0x5C } : new[] { 0xA3 }, HotkeySettings.DefaultKeys());

    [Fact]
    public void Normalize_EmptyHotkey_FallsBackToDefault()
    {
        var s = new AppSettings();
        s.Hotkey.Keys = [];
        Assert.Equal(HotkeySettings.DefaultKeys(), SettingsNormalizer.Normalize(s).Hotkey.Keys);
    }

    [Fact]
    public void Normalize_Vocabulary_TrimsDedupesAndLimitsTo1000()
    {
        var s = new AppSettings();
        s.Vocabulary = ["  SaySo ", "sayso", "", "   ", "Gemini", .. Enumerable.Range(0, 1500).Select(i => "term" + i)];
        var v = SettingsNormalizer.Normalize(s).Vocabulary;
        Assert.Equal("SaySo", v[0]);
        Assert.Equal("Gemini", v[1]);
        Assert.Equal(1000, v.Count);
    }

    [Fact]
    public void Normalize_LanguageCodes_TrimsAndDropsEmpty()
    {
        var s = new AppSettings();
        s.Recognition.LanguageCodes = [" ru-RU ", "", "ru-RU", "en-US"];
        Assert.Equal(new[] { "ru-RU", "en-US" }, SettingsNormalizer.Normalize(s).Recognition.LanguageCodes);
    }

    [Fact]
    public void Normalize_MicrophoneDeviceName_TrimsAndCapsAt256()
    {
        var s = new AppSettings { MicrophoneDeviceId = "id", MicrophoneDeviceName = "  Гарнитура  " };
        Assert.Equal("Гарнитура", SettingsNormalizer.Normalize(s).MicrophoneDeviceName);

        s.MicrophoneDeviceName = new string('я', 300);
        var name = SettingsNormalizer.Normalize(s).MicrophoneDeviceName;
        Assert.Equal(256, SettingsNormalizer.MaxDeviceNameLength);
        Assert.Equal(256, name!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankMicrophoneDeviceName_BecomesNull(string? input)
    {
        var s = new AppSettings { MicrophoneDeviceId = "id", MicrophoneDeviceName = input };
        Assert.Null(SettingsNormalizer.Normalize(s).MicrophoneDeviceName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_MicrophoneDeviceNameWithoutId_IsCleared(string? id)
    {
        // Имя без идентификатора бессмысленно: «по умолчанию» очищает оба значения.
        var s = new AppSettings { MicrophoneDeviceId = id, MicrophoneDeviceName = "Гарнитура" };
        var n = SettingsNormalizer.Normalize(s);
        Assert.Null(n.MicrophoneDeviceId);
        Assert.Null(n.MicrophoneDeviceName);
    }

    [Fact]
    public void TryLoad_FileLocked_ReportsFailure_SoCallerCanKeepCurrentSettings()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        var store = new SettingsStore(paths);
        var s = store.Load();
        s.Hotkey.Keys = [0xA2, 0x5B];
        store.Save(s);

        using (new HeldFile(paths.SettingsFile))
        {
            Assert.False(store.TryLoad(out var fallback));
            Assert.Equal(HotkeySettings.DefaultKeys(), fallback.Hotkey.Keys); // умолчания: применять их нельзя
        }

        Assert.True(store.TryLoad(out var loaded));
        Assert.Equal(new[] { 0xA2, 0x5B }, loaded.Hotkey.Keys);
    }

    [Fact]
    public void TryLoad_MissingFile_IsSuccess_WithDefaults()
    {
        using var dir = new TempDir();

        Assert.True(new SettingsStore(new AppPaths(dir.Path)).TryLoad(out var s));
        Assert.Equal(300, s.Hotkey.TapThresholdMs);
    }

    [Fact]
    public void TryLoad_CorruptJson_IsSuccess_WithDefaults_AndBackup()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path);
        File.WriteAllText(paths.SettingsFile, "{ это не json");

        Assert.True(new SettingsStore(paths).TryLoad(out var s));
        Assert.Equal(300, s.Hotkey.TapThresholdMs);
        Assert.True(File.Exists(paths.SettingsFile + ".bad"));
    }
}
