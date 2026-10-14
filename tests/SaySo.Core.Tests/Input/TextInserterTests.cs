using SaySo.Core.Input;

namespace SaySo.Core.Tests.Input;

public class TextInserterTests
{
    [Fact]
    public void Constructor_ZeroOwner_Throws() =>
        Assert.Throws<ArgumentException>(() => new TextInserter(0, TimeProvider.System));
}
