using SaySo.Core.Input;
using SaySo.Core.Interop;

namespace SaySo.Core.Tests.Input;

public class TextInserterTests
{
    [Fact]
    public void Constructor_ZeroOwner_Throws() =>
        Assert.Throws<ArgumentException>(() => new TextInserter(0, TimeProvider.System));

    [Fact]
    public void BelongsToProcess_OwnWindow_True()
    {
        var edit = TestWindows.CreateEdit();
        try
        {
            Assert.True(TextInserter.BelongsToProcess(edit, (uint)Environment.ProcessId));
            Assert.False(TextInserter.BelongsToProcess(edit, 0));
        }
        finally
        {
            Native.DestroyWindow(edit);
        }
    }

    [Fact]
    public void BelongsToProcess_ShellWindow_False() =>
        Assert.False(TextInserter.BelongsToProcess(Native.GetShellWindow(), (uint)Environment.ProcessId));
}
