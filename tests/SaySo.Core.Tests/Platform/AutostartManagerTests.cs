using Microsoft.Win32;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class AutostartManagerTests
{
    [Fact]
    public void Set_EnableThenDisable_InIsolatedKey()
    {
        var keyPath = @"Software\SaySoTests\Run-" + Guid.NewGuid().ToString("N");
        var manager = new AutostartManager(keyPath, "SaySo");
        const string exe = @"C:\Program Files\SaySo\SaySo.exe";
        try
        {
            Assert.False(manager.IsEnabled(exe));
            manager.Set(true, exe);
            Assert.True(manager.IsEnabled(exe));
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.Equal("\"" + exe + "\"", key!.GetValue("SaySo"));
            }

            Assert.False(manager.IsEnabled(@"C:\другой\SaySo.exe"));
            manager.Set(false, exe);
            Assert.False(manager.IsEnabled(exe));
            manager.Set(false, exe); // повторное выключение не падает
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\SaySoTests", throwOnMissingSubKey: false);
        }
    }
}
