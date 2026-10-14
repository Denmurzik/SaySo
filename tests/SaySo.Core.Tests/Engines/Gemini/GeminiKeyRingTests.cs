using SaySo.Core.Engines;
using SaySo.Core.Engines.Gemini;

namespace SaySo.Core.Tests.Engines.Gemini;

public class GeminiKeyRingTests
{
    private readonly ManualTimeProvider _time = new();

    private GeminiKeyRing Ring(params string[] keys)
    {
        var ring = new GeminiKeyRing(_time);
        ring.SetKeys(keys);
        return ring;
    }

    [Fact]
    public void Empty_AcquiresNothing()
    {
        var ring = Ring();
        Assert.Equal(0, ring.Count);
        Assert.Null(ring.Acquire());
    }

    [Fact]
    public void CurrentKey_IsSticky()
    {
        var ring = Ring("a", "b");
        Assert.Equal("a", ring.Acquire());
        Assert.Equal("a", ring.Acquire());
    }

    [Fact]
    public void Quota_SwitchesToNext_AndStaysThere_EvenAfterPause()
    {
        var ring = Ring("a", "b", "c");
        ring.ReportFailure("a", EngineErrorKind.QuotaExceeded);
        Assert.Equal("b", ring.Acquire());
        _time.Advance(GeminiKeyRing.QuotaPause + TimeSpan.FromSeconds(1));
        Assert.Equal("b", ring.Acquire()); // без «качелей» обратно на первый ключ
    }

    [Fact]
    public void AllPaused_ReturnsNull_UntilPauseEnds()
    {
        var ring = Ring("a", "b");
        ring.ReportFailure("a", EngineErrorKind.QuotaExceeded);
        ring.ReportFailure("b", EngineErrorKind.QuotaExceeded);
        Assert.Null(ring.Acquire());
        _time.Advance(GeminiKeyRing.QuotaPause);
        Assert.NotNull(ring.Acquire());
    }

    [Fact]
    public void InvalidKey_IsPausedForAnHour()
    {
        var ring = Ring("a", "b");
        ring.ReportFailure("a", EngineErrorKind.AuthFailed);
        ring.ReportFailure("b", EngineErrorKind.AuthFailed);
        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Null(ring.Acquire());
        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(ring.Acquire());
    }

    [Theory]
    [InlineData(EngineErrorKind.Network)]
    [InlineData(EngineErrorKind.RegionBlocked)]
    [InlineData(EngineErrorKind.Protocol)]
    [InlineData(EngineErrorKind.Timeout)]
    public void NonKeyErrors_DoNotPause(EngineErrorKind kind)
    {
        var ring = Ring("a", "b");
        ring.ReportFailure("a", kind);
        Assert.Equal("a", ring.Acquire());
    }

    [Fact]
    public void SetKeys_KeepsPausesAndCurrent_DropsRemoved_CleansInput()
    {
        var ring = Ring("a", "b", "c");
        ring.ReportFailure("a", EngineErrorKind.QuotaExceeded);
        Assert.Equal("b", ring.Acquire());
        ring.SetKeys([" a ", "", "b", "b", "d"]);
        Assert.Equal(3, ring.Count);
        Assert.Equal("b", ring.Acquire());
        ring.ReportFailure("b", EngineErrorKind.QuotaExceeded);
        Assert.Equal("d", ring.Acquire()); // «a» всё ещё на паузе
        Assert.Equal(3, ring.NumberOf("d"));
        Assert.Equal(0, ring.NumberOf("c"));
    }
}
