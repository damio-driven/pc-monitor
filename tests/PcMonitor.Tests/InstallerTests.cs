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
    // Security review: install where the user cannot replace the elevated binary.
    [Fact] public void Install_directory_is_under_eseguibili() => Assert.Equal(@"C:\Eseguibili\_\PcMonitor", TaskInstaller.InstallDirectory);

    [Fact] public void Protected_acl_gives_users_read_execute_only()
    {
        var sec = TaskInstaller.BuildProtectedDirectorySecurity();
        Assert.True(sec.AreAccessRulesProtected);   // no inheritance from C:\Eseguibili\_ (Authenticated Users: Modify)
        var rules = sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
                       .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        const System.Security.AccessControl.FileSystemRights write =
            System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData |
            System.Security.AccessControl.FileSystemRights.Delete | System.Security.AccessControl.FileSystemRights.ChangePermissions |
            System.Security.AccessControl.FileSystemRights.TakeOwnership | System.Security.AccessControl.FileSystemRights.WriteAttributes;
        var admins = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);
        foreach (var r in rules.Where(r => r.AccessControlType == System.Security.AccessControl.AccessControlType.Allow))
        {
            bool privileged = r.IdentityReference.Equals(admins) || r.IdentityReference.Equals(system);
            if (!privileged) Assert.True((r.FileSystemRights & write) == 0, $"{r.IdentityReference} has {r.FileSystemRights}");
        }
        Assert.Contains(rules, r => r.IdentityReference.Equals(admins) && r.FileSystemRights.HasFlag(System.Security.AccessControl.FileSystemRights.FullControl));
    }

    // Security review: the task XML is registered from memory (COM), no temp file another process could swap.
    [Fact] public void Task_xml_is_registered_without_temp_file()
    {
        const string name = "PcMonitor.SelfTest";
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?><Task version=""1.2"" xmlns=""{Ns}"">
<Principals><Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
<Settings><Enabled>false</Enabled></Settings>
<Actions Context=""Author""><Exec><Command>cmd.exe</Command><Arguments>/c exit</Arguments></Exec></Actions></Task>";
        try
        {
            TaskInstaller.RegisterTaskXml(name, xml);
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Query /TN \"{name}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
            p.WaitForExit();
            Assert.Equal(0, p.ExitCode);
        }
        finally
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Delete /F /TN \"{name}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!.WaitForExit();
        }
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
