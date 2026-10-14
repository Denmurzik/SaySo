using SaySo.Core.Updates;

namespace SaySo.Core.Tests.Updates;

public class UpdateInstallPlanTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    private static InstallEntry At(string dir) => new(dir);

    private static UpdateInstallPlan Decide(
        string exe, InstallEntry? machine = null, InstallEntry? user = null, bool installable = true, bool elevated = false) =>
        UpdateInstallPlan.Decide(exe, Roots, hive => hive == InstallHive.Machine ? machine : user, installable, elevated);

    [Fact]
    public void MachineKeyForThisDirectory_IsAllUsers()
    {
        var plan = Decide(@"C:\Program Files\SaySo\SaySo.exe", machine: At(@"C:\Program Files\SaySo"));

        Assert.Equal(UpdateInstallAction.Install, plan.Action);
        Assert.True(plan.AllUsers);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /ALLUSERS", plan.Arguments);
    }

    [Fact]
    public void UserKeyForThisDirectory_IsCurrentUser()
    {
        var plan = Decide(@"C:\Users\Ann\AppData\Local\Programs\SaySo\SaySo.exe", user: At(@"C:\Users\Ann\AppData\Local\Programs\SaySo"));

        Assert.Equal(UpdateInstallAction.Install, plan.Action);
        Assert.False(plan.AllUsers);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE /CURRENTUSER", plan.Arguments);
    }

    [Fact]
    public void ModeComesFromTheKey_NotFromTheFolderName()
    {
        // Установка «для всех» в нестандартную папку и «для меня» в Program Files: решает ключ, а не префикс пути.
        Assert.True(Decide(@"D:\Apps\SaySo\SaySo.exe", machine: At(@"D:\Apps\SaySo")).AllUsers);
        Assert.False(Decide(@"C:\Program Files\SaySo\SaySo.exe", user: At(@"C:\Program Files\SaySo")).AllUsers);
    }

    [Fact]
    public void BothKeysExist_TheOneForThisDirectoryWins()
    {
        var machineElsewhere = Decide(@"C:\Users\Ann\AppData\Local\Programs\SaySo\SaySo.exe",
            machine: At(@"C:\Program Files\SaySo"), user: At(@"C:\Users\Ann\AppData\Local\Programs\SaySo"));
        var userElsewhere = Decide(@"C:\Program Files\SaySo\SaySo.exe",
            machine: At(@"C:\Program Files\SaySo"), user: At(@"C:\Users\Ann\AppData\Local\Programs\SaySo"));

        Assert.False(machineElsewhere.AllUsers);
        Assert.Equal(UpdateInstallAction.Install, machineElsewhere.Action);
        Assert.True(userElsewhere.AllUsers);
    }

    [Theory]
    [InlineData(@"C:\Program Files\SaySo\", @"C:\Program Files\SaySo\SaySo.exe")]
    [InlineData(@"c:\program files\SAYSO", @"C:\Program Files\SaySo\SaySo.exe")]
    [InlineData(@"C:/Program Files/SaySo", @"C:\Program Files\SaySo\SaySo.exe")]
    [InlineData(@"C:\Program Files\SaySo", @"C:\Program Files\SaySo\..\SaySo\SaySo.exe")]
    [InlineData(@"C:\Program Files\SaySo", @"C:\Program Files\\SaySo\SaySo.exe")]
    public void KeyPathIsComparedNormalized(string registryPath, string exe)
    {
        var plan = Decide(exe, machine: At(registryPath));

        Assert.Equal(UpdateInstallAction.Install, plan.Action);
        Assert.True(plan.AllUsers);
    }

    [Fact]
    public void NoKey_NotInstalled_OpensReleasePage()
    {
        // Портативная копия, install.ps1 или сборка из исходников: ключа удаления нет.
        var plan = Decide(@"C:\Users\Ann\AppData\Local\Programs\SaySo\SaySo.exe");

        Assert.Equal(UpdateInstallAction.OpenReleasePage, plan.Action);
        Assert.Equal("", plan.Arguments);
    }

    [Fact]
    public void KeyForAnotherDirectory_ThisCopyIsNotTheInstalledOne()
    {
        var plan = Decide(@"C:\Tools\SaySo\SaySo.exe", machine: At(@"C:\Program Files\SaySo"), user: At(@"C:\Users\Ann\AppData\Local\Programs\SaySo"));

        Assert.Equal(UpdateInstallAction.OpenReleasePage, plan.Action);
    }

    [Theory]
    [InlineData(@"C:\Program Files\SaySo\SaySo.exe", true)]
    [InlineData(@"C:\Program Files (x86)\SaySo\SaySo.exe", true)]
    [InlineData(@"C:\Program Files Extra\SaySo\SaySo.exe", false)] // общий префикс строки — не вложенность
    [InlineData(@"D:\Program Files\SaySo\SaySo.exe", false)]
    [InlineData(@"C:\Users\Ann\AppData\Local\Programs\SaySo\SaySo.exe", false)]
    public void KeyWithoutPath_FallsBackToTheProgramFilesRule(string exe, bool allUsers)
    {
        var plan = Decide(exe, user: new InstallEntry(null));

        Assert.Equal(UpdateInstallAction.Install, plan.Action);
        Assert.Equal(allUsers, plan.AllUsers);
    }

    [Fact]
    public void BothKeysPointHere_FallsBackToTheProgramFilesRule()
    {
        Assert.True(Decide(@"C:\Program Files\SaySo\SaySo.exe", machine: At(@"C:\Program Files\SaySo"), user: At(@"C:\Program Files\SaySo")).AllUsers);
        Assert.False(Decide(@"D:\SaySo\SaySo.exe", machine: At(@"D:\SaySo"), user: At(@"D:\SaySo")).AllUsers);
    }

    [Fact]
    public void BrokenRegistryPath_IsNotAMatch()
    {
        var plan = Decide(@"C:\Program Files\SaySo\SaySo.exe", machine: At("C:\\Prog\0ram"));

        Assert.Equal(UpdateInstallAction.OpenReleasePage, plan.Action);
    }

    [Fact]
    public void ReleaseWithoutInstaller_OpensReleasePage()
    {
        var plan = Decide(@"C:\Program Files\SaySo\SaySo.exe", machine: At(@"C:\Program Files\SaySo"), installable: false);

        Assert.Equal(UpdateInstallAction.OpenReleasePage, plan.Action);
    }

    [Fact]
    public void ElevatedProcess_NeverInstalls_EvenForAnInstalledCopy()
    {
        var plan = Decide(@"C:\Program Files\SaySo\SaySo.exe", machine: At(@"C:\Program Files\SaySo"), elevated: true);

        Assert.Equal(UpdateInstallAction.OpenReleasePage, plan.Action);
        Assert.Equal("", plan.Arguments);
    }

    [Fact]
    public void RegistryIsNotReadWhenThePlanIsManualAnyway()
    {
        var reads = 0;

        UpdateInstallPlan.Decide(@"C:\Program Files\SaySo\SaySo.exe", Roots, _ => { reads++; return null; }, releaseInstallable: true, elevated: true);

        Assert.Equal(0, reads);
    }

    [Fact]
    public void SystemProgramFilesRoots_AreNotEmpty() => Assert.NotEmpty(UpdateInstallPlan.SystemProgramFilesRoots());

    [Fact]
    public void RealRegistry_ReadDoesNotThrow_AndKeyMatchesSetupAppId()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{4C827BFC-9A72-4512-910C-1DDC1336642F}_is1", InstallRegistry.UninstallKey);

        // Установлен SaySo или нет — читать можно в обоих кустах.
        _ = InstallRegistry.Read(InstallHive.Machine);
        _ = InstallRegistry.Read(InstallHive.User);
    }
}
