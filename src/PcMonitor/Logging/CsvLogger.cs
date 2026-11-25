using System.Globalization;
using System.Text;
using PcMonitor.Logic;
using PcMonitor.Model;

namespace PcMonitor.Logging;

public sealed record CsvRow(
    DateTime Time, double? T1, double? T0Max, double? PackageW, double? IaW,
    double? PerfPct, double? LoadPct, double? LimitPct, CpuState CpuState,
    double? Tzs0, double? Tzs1, double? TempD, double? TempC,
    double? ReadD, double? WriteD, double? ReadC, double? WriteC,
    int WheaDEp, int WheaDRp, int WheaCEp, int WheaCRp, int WheaOther,
    bool? OnAc, int? BatteryPct, double? RateW);

/// <summary>
/// Daily CSV (one row every 10 s) and events log. Every line is flushed so the
/// last rows survive a crash. IO errors are swallowed: logging never stops the app.
/// </summary>
public sealed class CsvLogger : IDisposable
{
    public const string Header = "ora;cpu_c;cpu_c_max;cpu_w;core_w;turbo_pct;carico_pct;limite_pct;stato_cpu;tzs0_c;tzs1_c;d_c;c_c;d_lett_mbs;d_scritt_mbs;c_lett_mbs;c_scritt_mbs;whea_d_ep;whea_d_rp;whea_c_ep;whea_c_rp;whea_altri;ac;batteria_pct;carica_w";

    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    readonly string _directory;
    readonly object _lock = new();
    StreamWriter? _csv;
    DateTime _csvDate;
    StreamWriter? _events;
    DateTime _eventsDate;

    public CsvLogger(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public void WriteRow(CsvRow r)
    {
        lock (_lock)
        {
            try
            {
                if (_csv is null || _csvDate != r.Time.Date)
                {
                    _csv?.Dispose();
                    _csv = Open(Path.Combine(_directory, $"pcmonitor-{r.Time:yyyyMMdd}.csv"), out bool isNew);
                    _csvDate = r.Time.Date;
                    if (isNew) _csv.WriteLine(Header);
                }
                _csv.WriteLine(Format(r));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _csv?.Dispose();
                _csv = null;
            }
        }
    }

    public void WriteEvent(JournalEntry e)
    {
        lock (_lock)
        {
            try
            {
                if (_events is null || _eventsDate != e.Time.Date)
                {
                    _events?.Dispose();
                    _events = Open(Path.Combine(_directory, $"events-{e.Time:yyyyMMdd}.log"), out _);
                    _eventsDate = e.Time.Date;
                }
                _events.WriteLine($"{e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}  {e.Text}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _events?.Dispose();
                _events = null;
            }
        }
    }

    public static void Cleanup(string directory, DateTime today, int keepDays = 7)
    {
        var oldestKept = today.Date.AddDays(-(keepDays - 1));
        try
        {
            foreach (var pattern in new[] { "pcmonitor-*.csv", "events-*.log" })
            {
                foreach (var file in Directory.EnumerateFiles(directory, pattern))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var datePart = name[(name.IndexOf('-') + 1)..];
                    if (DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        && d < oldestKept)
                    {
                        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static StreamWriter Open(string path, out bool isNew)
    {
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        isNew = fs.Length == 0;
        // Append to a non-empty file must not emit a second BOM.
        var w = new StreamWriter(fs, isNew ? Utf8Bom : new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
        return w;
    }

    static string Format(CsvRow r)
    {
        var sb = new StringBuilder(256);
        sb.Append(r.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
        foreach (var v in new[] { r.T1, r.T0Max, r.PackageW, r.IaW, r.PerfPct, r.LoadPct, r.LimitPct })
            sb.Append(';').Append(N(v));
        sb.Append(';').Append(r.CpuState.ToString());
        foreach (var v in new[] { r.Tzs0, r.Tzs1, r.TempD, r.TempC, r.ReadD, r.WriteD, r.ReadC, r.WriteC })
            sb.Append(';').Append(N(v));
        foreach (var n in new[] { r.WheaDEp, r.WheaDRp, r.WheaCEp, r.WheaCRp, r.WheaOther })
            sb.Append(';').Append(n.ToString(CultureInfo.InvariantCulture));
        sb.Append(';').Append(r.OnAc switch { true => "1", false => "0", null => "" });
        sb.Append(';').Append(r.BatteryPct?.ToString(CultureInfo.InvariantCulture) ?? "");
        sb.Append(';').Append(N(r.RateW));
        return sb.ToString();
    }

    static string N(double? v) => v is double d && double.IsFinite(d) ? d.ToString("F1", It) : "";

    public void Dispose()
    {
        lock (_lock)
        {
            _csv?.Dispose(); _csv = null;
            _events?.Dispose(); _events = null;
        }
    }
}
