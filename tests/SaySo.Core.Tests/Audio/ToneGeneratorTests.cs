using System.Buffers.Binary;
using SaySo.Core.Audio;

namespace SaySo.Core.Tests.Audio;

public class ToneGeneratorTests
{
    [Fact]
    public void CreateWav_LengthMatchesNotes()
    {
        var wav = ToneGenerator.CreateWav([(660, 60), (880, 90)]);
        Assert.Equal(44 + (60 + 90) * 32, wav.Length);
        Assert.Equal((60 + 90) * 32, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    [Fact]
    public void CreateWav_StartsAndEndsNearSilence_NoClicks()
    {
        var wav = ToneGenerator.CreateWav([(880, 100)], volume: 1f);
        var first = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44));
        var last = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(wav.Length - 2));
        Assert.InRange(Math.Abs((int)first), 0, 500);
        Assert.InRange(Math.Abs((int)last), 0, 500);
    }

    [Fact]
    public void CreateWav_RespectsVolume()
    {
        var wav = ToneGenerator.CreateWav([(440, 100)], volume: 0.25f);
        var max = 0;
        for (var i = 44; i < wav.Length; i += 2)
        {
            max = Math.Max(max, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(i))));
        }

        Assert.InRange(max, 7000, 8300); // 0.25 * 32767 ≈ 8192
    }

    [Fact]
    public void SoundBank_ProvidesThreeDistinctSounds()
    {
        Assert.True(SoundBank.Start.Length > 44);
        Assert.True(SoundBank.Stop.Length > 44);
        Assert.True(SoundBank.Error.Length > 44);
        Assert.NotEqual(SoundBank.Start, SoundBank.Stop);
        Assert.Same(SoundBank.Start, SoundBank.Start);
    }
}
