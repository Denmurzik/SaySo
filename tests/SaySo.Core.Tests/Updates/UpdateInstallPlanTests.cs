using SaySo.Core.Updates;

namespace SaySo.Core.Tests.Updates;

public class UpdateInstallPlanTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    private static UpdateInstallPlan Decide(string exe, bool installed = true, bool installable = true) =>
        UpdateInstallPlan.Decide(exe, Roots,
            path => installed && string.Equals(Path.GetFullPath(path),
                Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, "unins000.exe")), StringComparison.OrdinalIgnoreCase),
            installable);

    [Fact]
    public void ProgramFiles_IsAllUsers_AndElevated()
    {
        var plan = Decide(@"C:\Program Files\SaySo\SaySo.exe");

        Assert.Equal(UpdateInstallAction.Install, plan.Action);
        Assert.True(plan.AllUsers);
        Assert.True(plan.Elevate);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /ALLUSERS", plan.Arguments);
    }

    [Fact]
    public void ProgramFilesX86_IsAllUsers() =>
        Assert.True(Decide(@"C:\Program Files (x86)\SaySo\SaySo.exe").AllUsers);

    [Fact]
    public void PerUserInstall_IsCurrentUser_WithoutElevation()
    {
        var plan = Decide(@"C:\Users\Ann\AppData\Local\Programs\SaySo\SaySo.exe");

        Assert.Equal(UpdateInstallAction.Install, plan.Action);
        Assert.False(plan.AllUsers);
        Assert.False(plan.Elevate);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /CURRENTUSER", plan.Arguments);
    }

    [Theory]
    [InlineData(@"C:\Program Files Extra\SaySo\SaySo.exe")] // общий префикс строки — не вложенность
    [InlineData(@"D:\Program Files\SaySo\SaySo.exe")]
    [InlineData(@"C:\Tools\SaySo\SaySo.exe")]
    public void OutsideProgramFiles_IsCurrentUser(string exe) => Assert.False(Decide(exe).AllUsers);

    [Theory]
    [InlineData(@"c:\program files\saysO\SaySo.exe")]
    [InlineData(@"C:\Program Files\SaySo\..\SaySo\SaySo.exe")]
    [InlineData(@"C:\Program Files\\SaySo\SaySo.exe")]
    [InlineData(@"C:/Program Files/SaySo/SaySo.exe")]
    public void ProgramFilesCheck_IgnoresCaseAndPathSpelling(string exe) => Assert.True(Decide(exe).AllUsers);

    [Fact]
    public void NoUninstallerNextToExe_NotInstalled_OpensReleasePage()
    {
        // Портативная копия, install.ps1 или сборка из исходников: рядом нет unins000.exe.
        var plan = Decide(@"C:\Users\Ann\AppData\Local\Programs\SaySo\SaySo.exe", installed: false);

        Assert.Equal(UpdateInstallAction.OpenReleasePage, plan.Action);
        Assert.False(plan.Elevate);
        Assert.Equal("", plan.Arguments);
    }

    [Fact]
    public void NoUninstallerInProgramFiles_StillOpensReleasePage_NeverElevates()
    {
        var plan = Decide(@"C:\Program Files\SaySo\SaySo.exe", installed: false);

        Assert.Equal(UpdateInstallAction.OpenReleasePage, plan.Action);
        Assert.False(plan.Elevate);
    }

    [Fact]
    public void ReleaseWithoutInstaller_OpensReleasePage()
    {
        var plan = Decide(@"C:\Users\Ann\AppData\Local\Programs\SaySo\SaySo.exe", installable: false);

        Assert.Equal(UpdateInstallAction.OpenReleasePage, plan.Action);
    }

    [Fact]
    public void UninstallerIsLookedUpNextToTheExe()
    {
        string? asked = null;

        UpdateInstallPlan.Decide(@"C:\Program Files\SaySo\SaySo.exe", Roots, p => { asked = p; return true; }, true);

        Assert.Equal(@"C:\Program Files\SaySo\unins000.exe", asked);
    }

    [Fact]
    public void SystemProgramFilesRoots_AreNotEmpty() => Assert.NotEmpty(UpdateInstallPlan.SystemProgramFilesRoots());
}
