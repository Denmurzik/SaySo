using SaySo.Core.Audio;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Audio;

public class SoundThemeLoaderTests
{
    private static readonly byte[] FakeWav = [1, 2, 3, 4, 5];

    [Fact]
    public void Get_WithoutSelectedTheme_Throws()
    {
        var loader = new SoundThemeLoader(_ => new MemoryStream(FakeWav));

        Assert.Null(loader.ThemeId);
        Assert.Throws<InvalidOperationException>(() => loader.Get(SoundKind.Start));
    }

    [Fact]
    public void Select_ReadsNothingUntilASoundIsRequested()
    {
        var opened = new List<string>();
        var loader = Spy(opened);

        Assert.True(loader.Select("1-steklo"));

        Assert.Empty(opened);
        Assert.False(loader.IsLoaded(SoundKind.Start));
    }

    [Fact]
    public void Get_ReadsOnlyTheRequestedSoundOfTheSelectedTheme_AndCachesIt()
    {
        var opened = new List<string>();
        var loader = Spy(opened);
        loader.Select("1-steklo");

        var first = loader.Get(SoundKind.Start);
        var second = loader.Get(SoundKind.Start);

        Assert.Same(first, second);
        Assert.Equal(["SaySo.Sounds.1-steklo.start.wav"], opened);
        Assert.True(loader.IsLoaded(SoundKind.Start));
        Assert.False(loader.IsLoaded(SoundKind.Stop));
        Assert.False(loader.IsLoaded(SoundKind.Error));
    }

    [Fact]
    public void Get_ReturnsTheResourceBytes()
    {
        var loader = new SoundThemeLoader(_ => new MemoryStream(FakeWav));
        loader.Select("2-kaplya");

        Assert.Equal(FakeWav, loader.Get(SoundKind.Stop));
    }

    [Fact]
    public void Select_AnotherTheme_ReleasesThePreviousOneAndLoadsOnlyTheNewOne()
    {
        var opened = new List<string>();
        var loader = Spy(opened);
        loader.Select("1-steklo");
        var oldStart = loader.Get(SoundKind.Start);
        loader.Get(SoundKind.Stop);
        loader.Get(SoundKind.Error);
        opened.Clear();

        Assert.True(loader.Select("2-kaplya"));

        Assert.Equal("2-kaplya", loader.ThemeId);
        Assert.False(loader.IsLoaded(SoundKind.Start));
        Assert.False(loader.IsLoaded(SoundKind.Stop));
        Assert.False(loader.IsLoaded(SoundKind.Error));
        Assert.Empty(opened);

        var newStart = loader.Get(SoundKind.Start);

        Assert.NotSame(oldStart, newStart);
        Assert.Equal(["SaySo.Sounds.2-kaplya.start.wav"], opened);
        Assert.DoesNotContain(opened, n => n.Contains("1-steklo", StringComparison.Ordinal));
    }

    [Fact]
    public void Select_SameTheme_KeepsTheLoadedSounds()
    {
        var opened = new List<string>();
        var loader = Spy(opened);
        loader.Select("3-derevo");
        var start = loader.Get(SoundKind.Start);

        Assert.False(loader.Select("3-derevo"));

        Assert.True(loader.IsLoaded(SoundKind.Start));
        Assert.Same(start, loader.Get(SoundKind.Start));
        Assert.Single(opened);
    }

    [Fact]
    public void Get_MissingResource_ThrowsAndLoadsNothing()
    {
        var loader = new SoundThemeLoader(_ => null);
        loader.Select("1-steklo");

        Assert.Throws<InvalidOperationException>(() => loader.Get(SoundKind.Start));
        Assert.False(loader.IsLoaded(SoundKind.Start));
    }

    [Fact]
    public void Get_EmptyResource_Throws()
    {
        var loader = new SoundThemeLoader(_ => new MemoryStream());
        loader.Select("1-steklo");

        Assert.Throws<InvalidDataException>(() => loader.Get(SoundKind.Start));
    }

    [Fact]
    public void EmbeddedLoader_ReadsRealSounds_ForEveryThemeOneAtATime()
    {
        var loader = new SoundThemeLoader();

        foreach (var theme in SoundThemes.All)
        {
            loader.Select(theme.Id);
            foreach (var kind in new[] { SoundKind.Start, SoundKind.Stop, SoundKind.Error })
            {
                var wav = loader.Get(kind);
                Assert.True(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8), $"{theme.Id} {kind}");
            }
        }
    }

    /// <summary>Загрузчик настоящих ресурсов, который запоминает, какие имена у него просили.</summary>
    private static SoundThemeLoader Spy(List<string> opened) => new(name =>
    {
        opened.Add(name);
        return typeof(SoundThemes).Assembly.GetManifestResourceStream(name);
    });
}
