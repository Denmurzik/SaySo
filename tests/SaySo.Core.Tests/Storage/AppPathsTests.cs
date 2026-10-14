using SaySo.Core.Storage;

namespace SaySo.Core.Tests.Storage;

public class AppPathsTests
{
    private const string LocalAppData = @"C:\Users\tester\AppData\Local";

    [Fact]
    public void Resolve_WithoutOverride_UsesLocalAppDataSaySo()
    {
        Assert.Equal(@"C:\Users\tester\AppData\Local\SaySo", AppPaths.Resolve(null, LocalAppData).Root);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\dir")]
    [InlineData(@"..\data")]
    [InlineData(@"\no-drive")]
    public void Resolve_EmptyOrNotAbsoluteOverride_IsIgnored(string value)
    {
        Assert.Equal(@"C:\Users\tester\AppData\Local\SaySo", AppPaths.Resolve(value, LocalAppData).Root);
    }

    [Fact]
    public void Resolve_AbsoluteOverride_ReplacesTheDefaultFolder_AndFilesLiveInsideIt()
    {
        var paths = AppPaths.Resolve(@"  D:\scratch\sayso-demo  ", LocalAppData);

        Assert.Equal(@"D:\scratch\sayso-demo", paths.Root);
        Assert.Equal(@"D:\scratch\sayso-demo\settings.json", paths.SettingsFile);
    }

    [Fact]
    public void DataDirVariable_IsTheNameDocumentedInTheReadme()
    {
        Assert.Equal("SAYSO_DATA_DIR", AppPaths.DataDirVariable);
    }
}
