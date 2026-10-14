using SaySo.Core.Input;

namespace SaySo.Core.Tests.Input;

public class ProcessIntegrityTests
{
    [Fact]
    public void Current_IsAtLeastMedium() => Assert.True(ProcessIntegrity.Current >= 0x2000);

    [Fact]
    public void IsElevated_ConsistentWithIntegrity() =>
        Assert.Equal(ProcessIntegrity.Current >= 0x3000, ProcessIntegrity.IsElevated);

    [Fact]
    public void ForWindow_InvalidHandle_IsNull() => Assert.Null(ProcessIntegrity.ForWindow(0));
}
