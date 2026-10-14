using System.Buffers.Binary;
using SaySo.Core.Audio;

namespace SaySo.Core.Tests.Audio;

public class SoundThemeLoaderTests
{
    private static readonly SoundKind[] Kinds = [SoundKind.Start, SoundKind.Stop, SoundKind.Error];

    [Fact]
    public unsafe void Get_ReturnsPointerIntoTheResourceMemory_WithoutCopying()
    {
        var data = GC.AllocateArray<byte>(8, pinned: true);
        fixed (byte* p = data)
        {
            var address = (nint)p; // массив закреплён (POH), адрес стабилен и без fixed
            var loader = new SoundThemeLoader(_ => new UnmanagedMemoryStream((byte*)address, data.Length));

            Assert.Equal(address, loader.Get("1-steklo", SoundKind.Start));
        }
    }

    [Fact]
    public void Get_AsksOnlyForTheRequestedResource_AndCachesThePointer()
    {
        var opened = new List<string>();
        var loader = Spy(opened);

        var first = loader.Get("1-steklo", SoundKind.Start);
        var second = loader.Get("1-steklo", SoundKind.Start);

        Assert.Equal(first, second);
        Assert.Equal(["SaySo.Sounds.1-steklo.start.wav"], opened);
    }

    [Fact]
    public void Get_OtherThemeAndKind_OpensOnlyThatResource()
    {
        var opened = new List<string>();
        var loader = Spy(opened);
        var steklo = loader.Get("1-steklo", SoundKind.Start);
        opened.Clear();

        var kaplya = loader.Get("2-kaplya", SoundKind.Error);

        Assert.NotEqual(steklo, kaplya);
        Assert.Equal(["SaySo.Sounds.2-kaplya.error.wav"], opened);
    }

    [Fact]
    public void Get_MissingResource_Throws()
    {
        var loader = new SoundThemeLoader(_ => null);

        Assert.Throws<InvalidOperationException>(() => loader.Get("1-steklo", SoundKind.Start));
    }

    [Fact]
    public void Get_StreamWithoutDirectMemory_Throws()
    {
        var loader = new SoundThemeLoader(_ => new MemoryStream([1, 2, 3]));

        Assert.Throws<InvalidOperationException>(() => loader.Get("1-steklo", SoundKind.Start));
    }

    [Fact]
    public unsafe void Get_StreamNotAtStart_Throws()
    {
        var data = GC.AllocateArray<byte>(8, pinned: true);
        fixed (byte* p = data)
        {
            var address = (nint)p;
            var loader = new SoundThemeLoader(_ => new UnmanagedMemoryStream((byte*)address, data.Length) { Position = 4 });

            Assert.Throws<InvalidOperationException>(() => loader.Get("1-steklo", SoundKind.Start));
        }
    }

    [Fact]
    public unsafe void EmbeddedLoader_PointsAtTheWholeWav_ForEveryThemeAndKind()
    {
        var assembly = typeof(SoundThemes).Assembly;
        var loader = new SoundThemeLoader();

        foreach (var theme in SoundThemes.All)
        {
            foreach (var kind in Kinds)
            {
                var name = SoundThemes.ResourceName(theme.Id, kind);
                var address = loader.Get(theme.Id, kind);
                var pointer = (byte*)address;

                Assert.NotEqual(0, address);
                Assert.Equal(address, loader.Get(theme.Id, kind)); // тот же адрес при повторных вызовах
                Assert.True(new ReadOnlySpan<byte>(pointer, 4).SequenceEqual("RIFF"u8), $"{name}: нет RIFF по указателю");

                using var stream = assembly.GetManifestResourceStream(name)!;
                var expected = new byte[stream.Length];
                stream.ReadExactly(expected);
                Assert.Equal(expected.Length, BinaryPrimitives.ReadInt32LittleEndian(new ReadOnlySpan<byte>(pointer + 4, 4)) + 8);
                Assert.True(new ReadOnlySpan<byte>(pointer, expected.Length).SequenceEqual(expected), $"{name}: байты по указателю отличаются от ресурса");
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
