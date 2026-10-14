using System.Buffers.Binary;
using SaySo.Core.Audio;

namespace SaySo.Core.Tests.Audio;

public class SoundThemesTests
{
    private static readonly SoundKind[] Kinds = [SoundKind.Start, SoundKind.Stop, SoundKind.Error];

    [Fact]
    public void Catalogue_HasEightThemesInOrder_WithRussianNames()
    {
        Assert.Equal(
            new[] { "1-steklo", "2-kaplya", "3-derevo", "4-kolokolchik", "5-voylok", "6-podushka", "7-tap", "8-kalimba" },
            SoundThemes.All.Select(t => t.Id));
        Assert.Equal(
            new[] { "Стекло", "Капля", "Дерево", "Колокольчик", "Войлок", "Подушка", "Тап", "Калимба" },
            SoundThemes.All.Select(t => t.Name));
    }

    [Fact]
    public void Catalogue_NamesHaveNoDashes()
    {
        Assert.All(SoundThemes.All, t => Assert.DoesNotContain(t.Name, c => c is '—' or '–'));
    }

    [Fact]
    public void DefaultTheme_IsFeltAndInTheCatalogue()
    {
        Assert.Equal("5-voylok", SoundThemes.DefaultId);
        Assert.Contains(SoundThemes.All, t => t.Id == SoundThemes.DefaultId && t.Name == "Войлок");
    }

    [Theory]
    [InlineData("1-steklo", true)]
    [InlineData("8-kalimba", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("9-nope", false)]
    [InlineData("5-VOYLOK", false)]
    [InlineData(" 5-voylok", false)]
    public void IsKnown_MatchesCatalogueIdsExactly(string? id, bool expected) => Assert.Equal(expected, SoundThemes.IsKnown(id));

    [Theory]
    [InlineData("3-derevo", "3-derevo")]
    [InlineData("", "5-voylok")]
    [InlineData(null, "5-voylok")]
    [InlineData("мусор", "5-voylok")]
    public void Normalize_KeepsKnownAndReplacesTheRestWithDefault(string? id, string expected) =>
        Assert.Equal(expected, SoundThemes.Normalize(id));

    [Fact]
    public void ResourceName_IsStable()
    {
        Assert.Equal("SaySo.Sounds.5-voylok.start.wav", SoundThemes.ResourceName("5-voylok", SoundKind.Start));
        Assert.Equal("SaySo.Sounds.1-steklo.stop.wav", SoundThemes.ResourceName("1-steklo", SoundKind.Stop));
        Assert.Equal("SaySo.Sounds.8-kalimba.error.wav", SoundThemes.ResourceName("8-kalimba", SoundKind.Error));
    }

    [Fact]
    public void EveryThemeAndKind_IsEmbeddedAsValidWav()
    {
        var assembly = typeof(SoundThemes).Assembly;
        var names = assembly.GetManifestResourceNames().ToHashSet();

        foreach (var theme in SoundThemes.All)
        {
            foreach (var kind in Kinds)
            {
                var name = SoundThemes.ResourceName(theme.Id, kind);
                Assert.True(names.Contains(name), $"нет ресурса {name}");
                using var stream = assembly.GetManifestResourceStream(name)!;
                var wav = new byte[stream.Length];
                stream.ReadExactly(wav);
                AssertValidWav(name, wav);
            }
        }
    }

    [Fact]
    public void EmbeddedSounds_AreExactlyThoseOfTheCatalogue()
    {
        var expected = SoundThemes.All
            .SelectMany(t => Kinds.Select(k => SoundThemes.ResourceName(t.Id, k)))
            .Order()
            .ToArray();
        var actual = typeof(SoundThemes).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("SaySo.Sounds.", StringComparison.Ordinal))
            .Order()
            .ToArray();

        Assert.Equal(24, expected.Length);
        Assert.Equal(expected, actual);
    }

    /// <summary>RIFF/WAVE, PCM 48 кГц, моно, 16 бит; блок data занимает остаток файла (сигнал короче двух секунд).</summary>
    private static void AssertValidWav(string name, byte[] wav)
    {
        Assert.True(wav.Length > 44, $"{name}: слишком короткий файл");
        Assert.True(wav.AsSpan(0, 4).SequenceEqual("RIFF"u8), $"{name}: нет RIFF");
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.True(wav.AsSpan(8, 4).SequenceEqual("WAVE"u8), $"{name}: нет WAVE");
        Assert.True(wav.AsSpan(12, 4).SequenceEqual("fmt "u8), $"{name}: нет fmt");
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20))); // PCM
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22))); // моно
        Assert.Equal(48000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.True(wav.AsSpan(36, 4).SequenceEqual("data"u8), $"{name}: нет data на смещении 36");
        var dataBytes = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        Assert.Equal(wav.Length - 44, dataBytes);
        Assert.InRange(dataBytes / 2, 48000 / 10, 48000 * 2); // от 0,1 до 2 секунд
    }
}
