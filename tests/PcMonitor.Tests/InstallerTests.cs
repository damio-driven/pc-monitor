using PcMonitor.Install;

namespace PcMonitor.Tests;

public class InstallerTests
{
    [Fact] public void Create_args_exact()
        => Assert.Equal(@"/Create /F /TN ""PcMonitor"" /TR ""\""C:\Users\Damio\AppData\Local\PcMonitor\PcMonitor.exe\"""" /SC ONLOGON /RU ""Damio"" /RL HIGHEST /DELAY 0000:30 /IT",
                        TaskInstaller.BuildCreateArgs(@"C:\Users\Damio\AppData\Local\PcMonitor\PcMonitor.exe", "Damio"));
    [Fact] public void Delete_args_exact() => Assert.Equal(@"/Delete /F /TN ""PcMonitor""", TaskInstaller.BuildDeleteArgs());

    const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact] public void Task_xml_runs_on_battery_without_time_limit()
    {
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?><Task version=""1.2"" xmlns=""{Ns}""><Settings>
<DisallowStartIfOnBatteries>true</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>true</StopIfGoingOnBatteries>
<ExecutionTimeLimit>PT72H</ExecutionTimeLimit><Enabled>true</Enabled></Settings></Task>";
        var x = System.Xml.Linq.XDocument.Parse(TaskInstaller.FixTaskXml(xml));
        System.Xml.Linq.XNamespace n = Ns;
        var s = x.Root!.Element(n + "Settings")!;
        Assert.Equal("false", s.Element(n + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("false", s.Element(n + "StopIfGoingOnBatteries")!.Value);
        Assert.Equal("PT0S", s.Element(n + "ExecutionTimeLimit")!.Value);
    }
    [Fact] public void Task_xml_logon_trigger_limited_to_user()
    {
        var xml = $@"<Task xmlns=""{Ns}""><Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT30S</Delay></LogonTrigger></Triggers><Settings /></Task>";
        var x = System.Xml.Linq.XDocument.Parse(TaskInstaller.FixTaskXml(xml, @"PC\Damio"));
        System.Xml.Linq.XNamespace n = Ns;
        Assert.Equal(@"PC\Damio", x.Root!.Element(n + "Triggers")!.Element(n + "LogonTrigger")!.Element(n + "UserId")!.Value);
    }
    [Fact] public void Task_xml_missing_settings_are_added()
    {
        var x = System.Xml.Linq.XDocument.Parse(TaskInstaller.FixTaskXml($@"<Task xmlns=""{Ns}""><Settings><Enabled>true</Enabled></Settings></Task>"));
        System.Xml.Linq.XNamespace n = Ns;
        var s = x.Root!.Element(n + "Settings")!;
        Assert.Equal("false", s.Element(n + "DisallowStartIfOnBatteries")!.Value);
        Assert.Equal("PT0S", s.Element(n + "ExecutionTimeLimit")!.Value);
    }
}
