using SaySo.Core.Engines.Gemini;

namespace SaySo.Core.Tests.Engines.Gemini;

public class CompletionPolicyTests
{
    private static readonly GeminiServerEvent Final = new(GeminiEventKind.Final, "т");
    private static readonly GeminiServerEvent Interim = new(GeminiEventKind.Interim, "т");
    private static readonly GeminiServerEvent Turn = new(GeminiEventKind.TurnComplete);

    private static CompletionPolicy NewPolicy() => new(new CompletionTimings(QuietMs: 1200, QuietWithPendingInterimMs: 3000, HardTimeoutMs: 8000));

    [Fact]
    public void BeforeEnd_IsWaiting()
    {
        var p = NewPolicy();
        p.OnEvent(Final, 10);
        p.OnEvent(Turn, 20);
        Assert.Equal(CompletionState.Waiting, p.Evaluate(100_000));
    }

    [Fact]
    public void TurnCompleteAfterEnd_IsDoneImmediately()
    {
        var p = NewPolicy();
        p.OnEndSent(1000);
        p.OnEvent(Final, 1100);
        p.OnEvent(Turn, 1150);
        Assert.Equal(CompletionState.Done, p.Evaluate(1150));
    }

    [Fact]
    public void TurnCompleteBeforeEnd_IsIgnored()
    {
        var p = NewPolicy();
        p.OnEvent(Turn, 500);
        p.OnEndSent(1000);
        Assert.Equal(CompletionState.Waiting, p.Evaluate(1001));
    }

    [Fact]
    public void QuietPeriodAfterLastEvent_IsDone()
    {
        var p = NewPolicy();
        p.OnEndSent(0);
        p.OnEvent(Final, 100);
        Assert.Equal(CompletionState.Waiting, p.Evaluate(1299));
        Assert.Equal(CompletionState.Done, p.Evaluate(1300));
        Assert.Equal(1300, p.NextCheckAt());
    }

    [Fact]
    public void PendingInterim_ExtendsQuietPeriod_AndBlocksTurnComplete()
    {
        var p = NewPolicy();
        p.OnEndSent(0);
        p.OnEvent(Interim, 100);
        p.OnEvent(Turn, 150);
        Assert.True(p.HasPendingInterim);
        Assert.Equal(CompletionState.Waiting, p.Evaluate(3149));
        Assert.Equal(CompletionState.Done, p.Evaluate(3150));
    }

    [Fact]
    public void FinalAfterPendingInterim_WithTurnComplete_IsDone()
    {
        var p = NewPolicy();
        p.OnEndSent(0);
        p.OnEvent(Interim, 100);
        p.OnEvent(Turn, 150);
        p.OnEvent(Final, 400);
        Assert.Equal(CompletionState.Done, p.Evaluate(400));
    }

    [Fact]
    public void ContinuousEvents_HitHardTimeout()
    {
        var p = NewPolicy();
        p.OnEndSent(0);
        for (var t = 500; t <= 8000; t += 500)
        {
            p.OnEvent(Interim, t);
        }

        Assert.Equal(CompletionState.TimedOut, p.Evaluate(8000));
    }

    [Fact]
    public void NoTranscriptAtAll_WaitsForHardTimeout()
    {
        var p = NewPolicy();
        p.OnEndSent(5000);
        Assert.Equal(CompletionState.Waiting, p.Evaluate(6200));
        Assert.Equal(CompletionState.Waiting, p.Evaluate(12999));
        Assert.Equal(CompletionState.TimedOut, p.Evaluate(13000));
        Assert.Equal(13000, p.NextCheckAt());
    }

    [Fact]
    public void InterimBeforeEnd_ThenSilence_UsesPendingQuiet()
    {
        var p = NewPolicy();
        p.OnEvent(Interim, 100);
        p.OnEndSent(200);
        Assert.Equal(CompletionState.Waiting, p.Evaluate(3199));
        Assert.Equal(CompletionState.Done, p.Evaluate(3200));
    }
}
