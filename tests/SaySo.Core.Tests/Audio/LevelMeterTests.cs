using System.Buffers.Binary;
using SaySo.Core.Audio;

namespace SaySo.Core.Tests.Audio;

public class LevelMeterTests
{
    private static byte[] Sine(double amplitude, int samples = 1600)
    {
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var v = (short)(amplitude * 32767 * Math.Sin(2 * Math.PI * 440 * i / 16000.0));
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), v);
        }

        return bytes;
    }

    [Fact]
    public void Silence_IsZero() => Assert.Equal(0f, LevelMeter.Compute(new byte[3200]));

    [Fact]
    public void Empty_IsZero() => Assert.Equal(0f, LevelMeter.Compute(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void FullScaleSine_IsNearOne() => Assert.InRange(LevelMeter.Compute(Sine(1.0)), 0.93f, 1f);

    [Fact]
    public void Minus20dBSine_IsAboutTwoThirds()
    {
        // RMS синуса 0.1 амплитуды ≈ −23 dBFS → (−23 + 60) / 60 ≈ 0.62
        Assert.InRange(LevelMeter.Compute(Sine(0.1)), 0.58f, 0.66f);
    }

    [Fact]
    public void Louder_IsHigher() => Assert.True(LevelMeter.Compute(Sine(0.5)) > LevelMeter.Compute(Sine(0.05)));
}
