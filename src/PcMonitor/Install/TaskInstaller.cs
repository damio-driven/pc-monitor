using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;

namespace PcMonitor.Install;

/// <summary>
/// Scheduled task "PcMonitor": at logon of the current user, highest privileges, 30 s delay
/// (ESIF counters need admin; no UAC prompt at logon).
/// </summary>
public static class TaskInstaller
{
    public const string TaskName = "PcMonitor";
    const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>Chosen by the user next to the other portable tools; protected by <see cref="BuildProtectedDirectorySecurity"/>.</summary>
    public const string InstallDirectory = @"C:\Eseguibili\_\PcMonitor";

    public static string InstalledExe => Path.Combine(InstallDirectory, "PcMonitor.exe");

    public static string BuildCreateArgs(string exePath, string userName)
        => $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exePath}\\\"\" /SC ONLOGON /RU \"{userName}\" /RL HIGHEST /DELAY 0000:30 /IT";

    public static string BuildDeleteArgs() => $"/Delete /F /TN \"{TaskName}\"";

    /// <summary>
    /// schtasks /Create cannot set these, and its defaults break a laptop tray app:
    /// no start on battery, killed when unplugged, killed after 72 h.
    /// </summary>
    public static string FixTaskXml(string xml, string? logonUser = null)
    {
        var doc = XDocument.Parse(xml);
        XNamespace n = TaskNs;
        var root = doc.Root ?? throw new InvalidDataException("task XML without root");
        // schtasks /SC ONLOGON leaves the trigger open to any user: limit it to ours.
        if (logonUser != null)
        {
            foreach (var trigger in root.Elements(n + "Triggers").Elements(n + "LogonTrigger"))
            {
                var uid = trigger.Element(n + "UserId");
                if (uid is null) trigger.Add(new XElement(n + "UserId", logonUser)); else uid.Value = logonUser;
            }
        }
        var settings = root.Element(n + "Settings");
        if (settings is null) { settings = new XElement(n + "Settings"); root.Add(settings); }
        void Set(string name, string value)
        {
            var e = settings.Element(n + name);
            if (e is null) settings.Add(new XElement(n + name, value)); else e.Value = value;
        }
        Set("DisallowStartIfOnBatteries", "false");
        Set("StopIfGoingOnBatteries", "false");
        Set("ExecutionTimeLimit", "PT0S");
        var sb = new StringBuilder();
        using (var w = new Utf16Writer(sb)) doc.Save(w);
        return sb.ToString();
    }

    sealed class Utf16Writer : StringWriter
    {
        public Utf16Writer(StringBuilder sb) : base(sb) { }
        public override Encoding Encoding => Encoding.Unicode;
    }

    /// <summary>
    /// Protected ACL for the install folder: SYSTEM and Administrators full control, Users read and
    /// execute, no inheritance (C:\Eseguibili\_ grants Authenticated Users Modify). The elevated task
    /// runs this exe, so a non-elevated process must not be able to replace or rename it.
    /// Residual risk accepted by the user: the parent folder itself can still be renamed without UAC.
    /// </summary>
    public static DirectorySecurity BuildProtectedDirectorySecurity()
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.SetOwner(admins);
        return sec;
    }

    /// <summary>
    /// Makes <paramref name="path"/> a protected, empty folder owned by Administrators, safe to copy the
    /// elevated exe into. A missing folder is created with the protected ACL in one step. An existing one
    /// may have been planted by a non-elevated process (the parent grants Authenticated Users Modify):
    /// a reparse point (junction/symlink) is refused, otherwise the ACL is applied first (locking the
    /// planter out), the folder re-checked, and every entry deleted (a planted hardlink is unlinked,
    /// never written through). <paramref name="keep"/> = a file not to delete (the running exe).
    /// </summary>
    public static void PrepareInstallDirectory(string path, string? keep = null)
    {
        var sec = BuildProtectedDirectorySecurity();
        var dir = new DirectoryInfo(path);
        if (!dir.Exists && !File.Exists(path)) dir.Create(sec);
        dir.Refresh();
        EnsurePlainDirectory(dir);
        dir.SetAccessControl(sec);
        dir.Refresh();
        EnsurePlainDirectory(dir);
        var owner = dir.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (owner is not SecurityIdentifier sid ||
            !(sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || sid.IsWellKnown(WellKnownSidType.LocalSystemSid)))
            throw new IOException($"{path}: proprietario inatteso {owner}");

        foreach (var entry in dir.EnumerateFileSystemInfos())
        {
            if (keep != null && string.Equals(entry.FullName, Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) continue;
            if (entry is DirectoryInfo d)
            {
                if (d.Attributes.HasFlag(FileAttributes.ReparsePoint)) d.Delete();   // removes the link, not its target
                else d.Delete(recursive: true);
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }
    }

    static void EnsurePlainDirectory(DirectoryInfo dir)
    {
        if (!dir.Exists) throw new IOException($"{dir.FullName}: non e' una cartella");
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException($"{dir.FullName} e' un collegamento (junction/symlink): installazione rifiutata");
    }

    /// <summary>
    /// Registers (create or update) a task from XML held in memory through the Task Scheduler COM
    /// API: no temp file another process could swap before the elevated registration reads it.
    /// </summary>
    public static void RegisterTaskXml(string name, string xml)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Task Scheduler non disponibile");
        dynamic? service = null, folder = null, task = null;
        try
        {
            service = Activator.CreateInstance(type)!;
            service.Connect();
            folder = service.GetFolder("\\");
            const int TASK_CREATE_OR_UPDATE = 6, TASK_LOGON_INTERACTIVE_TOKEN = 3;
            task = folder.RegisterTask(name, xml, TASK_CREATE_OR_UPDATE, null, null, TASK_LOGON_INTERACTIVE_TOKEN, null);
        }
        finally
        {
            foreach (var o in new object?[] { task, folder, service })
                if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
        }
    }

    /// <summary>Copies the running exe to the protected install folder, registers the task, starts the installed copy.</summary>
    public static int Install(Action<string> report)
    {
        if (!AppPaths.IsElevated()) return RelaunchElevated("--install", report);

        if (Mutex.TryOpenExisting(SingleInstance.MutexName, out var running))
        {
            running.Dispose();
            report("Chiudi PcMonitor (menu Esci) e riprova.");
            return 1;
        }

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("process path unknown");
        var target = InstalledExe;
        try
        {
            bool selfInstall = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            PrepareInstallDirectory(InstallDirectory, keep: selfInstall ? target : null);
            if (!selfInstall) File.Copy(source, target, overwrite: false);   // folder is empty and protected
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report($"Installazione in {InstallDirectory} non riuscita: {e.Message}");
            return 1;
        }

        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var (rc, output) = RunSchtasks(BuildCreateArgs(target, user));
        if (rc != 0) { report($"schtasks /Create non riuscito ({rc}): {output}"); return rc; }

        // Re-register from XML with battery / time-limit / trigger-user settings fixed.
        var (qrc, xml) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
        if (qrc == 0 && xml.Contains("<Task", StringComparison.Ordinal))
        {
            try { RegisterTaskXml(TaskName, FixTaskXml(xml.Trim(), user)); }
            catch (Exception e) { report($"Attenzione: impostazioni batteria non applicate: {e.Message}"); }
        }
        else
        {
            report($"Attenzione: impossibile leggere l'attivita' per correggere le impostazioni batteria ({qrc}).");
        }

        RemoveOldCopy();
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = InstallDirectory }); }
        catch (Exception e) { report($"Installato, ma avvio non riuscito: {e.Message}"); }
        report($"PcMonitor installato in {target}; attivita' pianificata \"{TaskName}\" creata (all'accesso, privilegi piu' elevati, ritardo 30 s).");
        return 0;
    }

    /// <summary>Earlier builds installed into %LOCALAPPDATA%\PcMonitor (user-writable): remove that exe, keep the logs.</summary>
    static void RemoveOldCopy()
    {
        var old = Path.Combine(AppPaths.AppDirectory, "PcMonitor.exe");
        try { if (File.Exists(old)) File.Delete(old); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static int Uninstall(Action<string> report)
    {
        if (!AppPaths.IsElevated()) return RelaunchElevated("--uninstall", report);
        var (rc, output) = RunSchtasks(BuildDeleteArgs());
        report(rc == 0 ? $"Attivita' pianificata \"{TaskName}\" rimossa. Il programma resta in {InstallDirectory}, i log in {AppPaths.LogDirectory}."
                       : $"schtasks /Delete non riuscito ({rc}): {output}");
        return rc;
    }

    static int RelaunchElevated(string arg, Action<string> report)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arg) { UseShellExecute = true, Verb = "runas" });
            p?.WaitForExit();
            return p?.ExitCode ?? 1;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            report("Operazione annullata: servono i privilegi di amministratore.");
            return 1;
        }
    }

    static (int ExitCode, string Output) RunSchtasks(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Default, StandardErrorEncoding = Encoding.Default,
        };
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, (outTask.Result + errTask.Result).Trim());
    }
}
