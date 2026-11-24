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
    /// Makes <paramref name="path"/> a protected folder (owner Administrators, protected ACL), with no
    /// files left in it, safe to copy the elevated exe into. Untrusted content is never modified:
    /// an existing entry that is not a folder created by a previous install (wrong owner, unprotected
    /// ACL, write access for non-admins, or a junction/symlink/file) is renamed aside - a rename acts
    /// on the link itself and follows nothing - and a fresh folder is created with the protected ACL
    /// in one step. A trusted folder (only admins could have written into it) keeps its place and
    /// loses its top-level files. Returns the path the untrusted entry was moved to, or null.
    /// </summary>
    public static string? PrepareInstallDirectory(string path)
    {
        string? aside = null;
        var dir = new DirectoryInfo(path);
        if (dir.Exists || File.Exists(path))
        {
            if (IsTrustedInstallDirectory(dir))
            {
                foreach (var f in dir.EnumerateFiles())
                {
                    f.Attributes = FileAttributes.Normal;
                    f.Delete();
                }
            }
            else
            {
                aside = $"{path}.old-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
                if (dir.Exists) Directory.Move(path, aside); else File.Move(path, aside);
            }
        }
        dir = new DirectoryInfo(path);
        if (!dir.Exists) dir.Create(BuildProtectedDirectorySecurity());
        dir.Refresh();
        if (!IsTrustedInstallDirectory(dir)) throw new IOException($"{path}: cartella d'installazione non protetta");
        return aside;
    }

    /// <summary>A real folder owned by Administrators/SYSTEM, protected ACL, no write access for anyone else.</summary>
    static bool IsTrustedInstallDirectory(DirectoryInfo dir)
    {
        if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        var acl = dir.GetAccessControl();
        if (!acl.AreAccessRulesProtected) return false;
        static bool Privileged(IdentityReference? id) => id is SecurityIdentifier s &&
            (s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || s.IsWellKnown(WellKnownSidType.LocalSystemSid));
        if (!Privileged(acl.GetOwner(typeof(SecurityIdentifier)))) return false;
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes |
                                       FileSystemRights.WriteAttributes | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete |
                                       FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule r in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (r.AccessControlType == AccessControlType.Allow && !Privileged(r.IdentityReference) && (r.FileSystemRights & write) != 0)
                return false;
        return true;
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
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(source)), Path.GetFullPath(InstallDirectory), StringComparison.OrdinalIgnoreCase))
            {
                report("Avvia --install dall'exe pubblicato (cartella publish), non da quello installato.");
                return 1;
            }
            var aside = PrepareInstallDirectory(InstallDirectory);
            if (aside != null) report($"Attenzione: {InstallDirectory} esisteva con permessi non sicuri ed e' stata spostata in {aside} (non modificata): controllala e cancellala.");
            File.Copy(source, target, overwrite: false);   // folder is protected and has no files
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
