using SaySo.Core.Engines.Gemini;

namespace SaySo.Core.Tests.Engines.Gemini;

public class TranscriptAssemblerTests
{
    [Fact]
    public void IncrementalFinals_AreJoinedWithSpace()
    {
        var a = new TranscriptAssembler();
        a.AddFinal("Привет.");
        a.AddFinal(" Как дела? ");
        Assert.Equal("Привет. Как дела?", a.BuildResult(includePendingInterim: true));
        Assert.Equal(2, a.FinalCount);
    }

    [Fact]
    public void CumulativeFinals_ReplaceAccumulatedText()
    {
        var a = new TranscriptAssembler();
        a.AddFinal("Привет.");
        a.AddFinal("Привет. Как дела?");
        Assert.Equal("Привет. Как дела?", a.BuildResult(false));
    }

    [Fact]
    public void WhitespaceFinal_IsIgnored()
    {
        var a = new TranscriptAssembler();
        a.AddFinal("   ");
        Assert.Equal(0, a.FinalCount);
        Assert.Equal("", a.BuildResult(true));
    }

    [Fact]
    public void Final_ClearsPendingInterim()
    {
        var a = new TranscriptAssembler();
        a.SetInterim("Встре");
        Assert.Equal("Встре", a.PendingInterim);
        a.AddFinal("Встретимся.");
        Assert.Null(a.PendingInterim);
        Assert.Equal("Встретимся.", a.BuildResult(true));
    }

    [Fact]
    public void OnlyInterim_UsedWhenRequested()
    {
        var a = new TranscriptAssembler();
        a.SetInterim("незаконченная фраза");
        Assert.Equal("незаконченная фраза", a.BuildResult(includePendingInterim: true));
        Assert.Equal("", a.BuildResult(includePendingInterim: false));
    }

    [Fact]
    public void FinalsPlusPendingInterim_AreJoined()
    {
        var a = new TranscriptAssembler();
        a.AddFinal("Первое.");
        a.SetInterim("второе");
        Assert.Equal("Первое. второе", a.BuildResult(true));
    }

    [Fact]
    public void EmptyInterim_ClearsPending()
    {
        var a = new TranscriptAssembler();
        a.SetInterim("что-то");
        a.SetInterim("  ");
        Assert.Null(a.PendingInterim);
    }
}
