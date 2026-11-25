using PcMonitor.Logging;
using PcMonitor.Logic;
using PcMonitor.Model;

namespace PcMonitor.Tests;

public sealed class CsvLoggerTests : IDisposable
{
    readonly List<string> _dirs = new();

    string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "PcMonitorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    public void Dispose()
    {
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch (IOException) { }
    }

    static CsvRow Row(DateTime t) => new(t, 84.5, 92, 28, 21, 215, 40, 100, CpuState.Turbo, 65, 50, 51, 55, 0, 0, 1.2, 3, 0, 0, 0, 0, 0, true, 66, 26);

    [Fact] public void Writes_header_and_italian_row()
    {
        var dir = TempDir(); using (var l = new CsvLogger(dir)) l.WriteRow(Row(new DateTime(2026, 11, 24, 17, 0, 5)));
        var lines = File.ReadAllLines(Path.Combine(dir, "pcmonitor-20261124.csv"));
        Assert.Equal(CsvLogger.Header, lines[0]);
        Assert.StartsWith("17:00:05;84,5;92,0;28,0;21,0;215,0;40,0;100,0;Turbo;", lines[1]);
        Assert.EndsWith(";1;66;26,0", lines[1]);
    }
    [Fact] public void Null_values_are_empty_fields()
    {
        var dir = TempDir(); using (var l = new CsvLogger(dir)) l.WriteRow(Row(DateTime.Today) with { T1 = null, OnAc = null });
        var f = File.ReadAllLines(Path.Combine(dir, $"pcmonitor-{DateTime.Today:yyyyMMdd}.csv"))[1].Split(';');
        Assert.Equal("", f[1]); Assert.Equal("", f[22]);
    }
    [Fact] public void Switches_file_after_midnight()
    {
        var dir = TempDir(); using (var l = new CsvLogger(dir)) { l.WriteRow(Row(new DateTime(2026, 11, 24, 23, 59, 55))); l.WriteRow(Row(new DateTime(2026, 11, 25, 0, 0, 5))); }
        Assert.True(File.Exists(Path.Combine(dir, "pcmonitor-20261124.csv")));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "pcmonitor-20261125.csv")).Length);
    }
    [Fact] public void Row_is_readable_while_logger_open()   // flush per line, crash safety
    {
        var dir = TempDir(); using var l = new CsvLogger(dir); l.WriteRow(Row(new DateTime(2026, 11, 24, 10, 0, 0)));
        using var fs = new FileStream(Path.Combine(dir, "pcmonitor-20261124.csv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Equal(2, new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }
    [Fact] public void Cleanup_keeps_7_days()
    {
        var dir = TempDir();
        foreach (var d in new[] { "20261116", "20261117", "20261118", "20261124" }) { File.WriteAllText(Path.Combine(dir, $"pcmonitor-{d}.csv"), ""); File.WriteAllText(Path.Combine(dir, $"events-{d}.log"), ""); }
        CsvLogger.Cleanup(dir, new DateTime(2026, 11, 24));
        Assert.False(File.Exists(Path.Combine(dir, "pcmonitor-20261116.csv")));
        Assert.False(File.Exists(Path.Combine(dir, "events-20261117.log")));
        Assert.True(File.Exists(Path.Combine(dir, "pcmonitor-20261118.csv")));
    }
    [Fact] public void Writes_event_line()
    {
        var dir = TempDir(); using (var l = new CsvLogger(dir)) l.WriteEvent(new JournalEntry(new DateTime(2026, 11, 24, 17, 40, 12), "CPU freno termico, 96 C, durata 45 s"));
        Assert.Equal("17:40:12  CPU freno termico, 96 C, durata 45 s", File.ReadAllLines(Path.Combine(dir, "events-20261124.log"))[0]);
    }
}
