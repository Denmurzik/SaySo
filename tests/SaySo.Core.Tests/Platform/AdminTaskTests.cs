using System.Xml.Linq;
using SaySo.Core.Platform;

namespace SaySo.Core.Tests.Platform;

public class AdminTaskTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void BuildXml_RunsAtLogonWithHighestPrivileges_OnBattery_WithoutTimeLimit()
    {
        const string exe = @"C:\Users\Тест & Co\AppData\Local\Programs\SaySo\SaySo.exe";
        var xml = AdminTask.BuildXml(exe, @"PC\Тест");
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
        var doc = XDocument.Parse(xml);
        var task = doc.Root!;
        Assert.Equal(exe, task.Element(Ns + "Actions")!.Element(Ns + "Exec")!.Element(Ns + "Command")!.Value);
        Assert.Equal(@"PC\Тест", task.Element(Ns + "Triggers")!.Element(Ns + "LogonTrigger")!.Element(Ns + "UserId")!.Value);
        var principal = task.Element(Ns + "Principals")!.Element(Ns + "Principal")!;
        Assert.Equal("HighestAvailable", principal.Element(Ns + "RunLevel")!.Value);
        Assert.Equal("InteractiveToken", principal.Element(Ns + "LogonType")!.Value);
        var settings = task.Element(Ns + "Settings")!;
        Assert.Equal("false", settings.Element(Ns + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", settings.Element(Ns + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", settings.Element(Ns + "ExecutionTimeLimit")!.Value);
        Assert.Equal("IgnoreNew", settings.Element(Ns + "MultipleInstancesPolicy")!.Value);
        Assert.Equal("4", settings.Element(Ns + "Priority")!.Value);
    }
}
