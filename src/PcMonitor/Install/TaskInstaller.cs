using System.Diagnostics;
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

    public static string InstalledExe => Path.Combine(AppPaths.AppDirectory, "PcMonitor.exe");

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

    /// <summary>Copies the running exe to %LOCALAPPDATA%\PcMonitor, registers the task, starts the installed copy.</summary>
    public static int Install(Action<string> report)
    {
        if (!AppPaths.IsElevated()) return RelaunchElevated("--install", report);

        var source = Environment.ProcessPath ?? throw new InvalidOperationException("process path unknown");
        var target = InstalledExe;
        Directory.CreateDirectory(AppPaths.AppDirectory);
        if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            try { File.Copy(source, target, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report("Chiudi PcMonitor (menu Esci) e riprova.");
                return 1;
            }
        }

        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var (rc, output) = RunSchtasks(BuildCreateArgs(target, user));
        if (rc != 0) { report($"schtasks /Create non riuscito ({rc}): {output}"); return rc; }

        // Re-create from XML with battery/time-limit settings fixed.
        var (qrc, xml) = RunSchtasks($"/Query /TN \"{TaskName}\" /XML");
        if (qrc == 0 && xml.Contains("<Task", StringComparison.Ordinal))
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"PcMonitor-task-{Environment.ProcessId}.xml");
            try
            {
                File.WriteAllText(tmp, FixTaskXml(xml.Trim(), user), Encoding.Unicode);
                var (xrc, xout) = RunSchtasks($"/Create /F /TN \"{TaskName}\" /XML \"{tmp}\"");
                if (xrc != 0) report($"Attenzione: impostazioni batteria non applicate ({xrc}): {xout}");
            }
            finally
            {
                try { File.Delete(tmp); } catch (IOException) { }
            }
        }
        else
        {
            report($"Attenzione: impossibile leggere l'attivita' per correggere le impostazioni batteria ({qrc}).");
        }

        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = AppPaths.AppDirectory }); }
        catch (Exception e) { report($"Installato, ma avvio non riuscito: {e.Message}"); }
        report($"PcMonitor installato in {target}; attivita' pianificata \"{TaskName}\" creata (all'accesso, privilegi piu' elevati, ritardo 30 s).");
        return 0;
    }

    public static int Uninstall(Action<string> report)
    {
        if (!AppPaths.IsElevated()) return RelaunchElevated("--uninstall", report);
        var (rc, output) = RunSchtasks(BuildDeleteArgs());
        report(rc == 0 ? $"Attivita' pianificata \"{TaskName}\" rimossa. Il programma resta in {AppPaths.AppDirectory}."
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
