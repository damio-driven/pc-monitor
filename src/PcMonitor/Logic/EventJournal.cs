using System.Globalization;
using PcMonitor.Model;

namespace PcMonitor.Logic;

public sealed record JournalEntry(DateTime Time, string Text);

/// <summary>State-change journal (newest first), with CPU episode durations.</summary>
public sealed class EventJournal
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    readonly object _lock = new();
    readonly LinkedList<JournalEntry> _entries = new();
    readonly int _capacity;

    CpuState _cpuState = CpuState.Unknown;
    DateTime _episodeStart;
    double? _episodePeak;
    SsdState _ssdState = SsdState.Unknown;

    public EventJournal(int capacity = 200) => _capacity = capacity;

    public event Action<JournalEntry>? Added;

    public IReadOnlyList<JournalEntry> Entries
    {
        get { lock (_lock) return _entries.ToList(); }
    }

    public void Add(DateTime t, string text)
    {
        var e = new JournalEntry(t, text);
        lock (_lock)
        {
            _entries.AddFirst(e);
            while (_entries.Count > _capacity) _entries.RemoveLast();
        }
        Added?.Invoke(e);
    }

    public void ObserveCpu(DateTime now, CpuState s, double? t1)
    {
        string? text = null;
        lock (_lock)
        {
            if (s == _cpuState)
            {
                if (t1 is double v && (_episodePeak is null || v > _episodePeak)) _episodePeak = v;
                return;
            }
            int seconds = (int)Math.Round((now - _episodeStart).TotalSeconds);
            if (_cpuState == CpuState.Thermal)
                text = $"CPU freno termico, {(_episodePeak is double p ? p.ToString("0", It) : "n/d")} C, durata {seconds} s";
            else if (_cpuState == CpuState.PowerLimit)
                text = $"CPU limite di potenza, durata {seconds} s";
            _cpuState = s;
            _episodeStart = now;
            _episodePeak = t1;
        }
        if (text != null) Add(now, text);
    }

    public void ObserveSsd(DateTime now, SsdState s, string detail)
    {
        string? text = null;
        lock (_lock)
        {
            if (s == _ssdState) return;
            if (s == SsdState.Hot) text = $"SSD caldo: {detail}";
            else if (s == SsdState.Whea) text = $"Errori PCIe: {detail}";
            _ssdState = s;
        }
        if (text != null) Add(now, text);
    }
}
