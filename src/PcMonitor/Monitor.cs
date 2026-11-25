using System.Globalization;
using System.Security.Principal;
using PcMonitor.Logging;
using PcMonitor.Logic;
using PcMonitor.Model;
using PcMonitor.Sensors;
using PcMonitor.Whea;

namespace PcMonitor;

public sealed record MonitorSnapshot(
    DateTime Time, CpuReading Cpu, CpuState CpuState, double? T1Min, double? T1Max,
    IReadOnlyList<DiskReading> Disks, SsdState SsdState, PowerReading Power,
    bool Elevated, bool EsifAvailable)
{
    /// <summary>Set on the tick a WHEA burst starts, at most once per 10 minutes: text for the notification.</summary>
    public string? BurstMessage { get; init; }
}

public interface ISensors
{
    CpuReading ReadCpu();
    IReadOnlyList<DiskReading> ReadDisks(bool refreshTemps);
    PowerReading ReadPower(bool refreshRate);
    bool EsifAvailable => true;
}

public static class AppPaths
{
    public static string AppDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PcMonitor");

    public static string LogDirectory { get; } = Path.Combine(AppDirectory, "logs");

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Windows boot time (Environment.TickCount64 includes sleep time).</summary>
    public static DateTime BootTime() => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
}

/// <summary>Real hardware sources. Topology is read once at construction.</summary>
public sealed class RealSensors : ISensors, IDisposable
{
    readonly CpuSensor _cpu = new();
    readonly DiskSensor _disks;

    public RealSensors()
    {
        IReadOnlyList<DiskInfo> topology;
        try { topology = DiskTopology.Discover(); }
        catch (Exception) { topology = Array.Empty<DiskInfo>(); }
        _disks = new DiskSensor(topology);
    }

    public IReadOnlyList<DiskInfo> Disks => _disks.Disks;
    public bool EsifAvailable => _cpu.EsifAvailable;
    public CpuReading ReadCpu() => _cpu.Read();
    public IReadOnlyList<DiskReading> ReadDisks(bool refreshTemps) => _disks.Read(refreshTemps);
    public PowerReading ReadPower(bool refreshRate) => PowerSensor.Read(refreshRate);

    public void Dispose()
    {
        _cpu.Dispose();
        _disks.Dispose();
    }
}

/// <summary>
/// Orchestrator: polls every second on a thread-pool timer, classifies, keeps history,
/// journals state changes and writes the CSV every 10 s. A failing source yields null
/// readings for that tick; nothing escapes <see cref="Tick"/>.
/// </summary>
public sealed class Monitor : IDisposable
{
    public const int HistoryLength = 600;
    public const int TempRefreshTicks = 5;
    public const int SlowRefreshTicks = 10;

    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    readonly ISensors _sensors;
    readonly CsvLogger? _log;
    readonly Func<DateTime> _clock;
    readonly CpuStateClassifier _classifier = new();
    readonly bool _elevated;
    readonly object _tickLock = new();
    System.Threading.Timer? _timer;
    int _tickIndex;
    int _running;

    IReadOnlyList<DiskReading> _lastDisks = Array.Empty<DiskReading>();
    PowerReading _lastPower = PowerReading.Empty;
    double? _t1Min, _t1Max, _t0PeriodMax;
    bool _inBurst;

    public Monitor(ISensors s, WheaTracker w, EventJournal j, CsvLogger? log, Func<DateTime> clock)
    {
        _sensors = s;
        Whea = w;
        Journal = j;
        _log = log;
        _clock = clock;
        _elevated = SafeIsElevated();
        if (_log != null) Journal.Added += OnJournalAdded;
    }

    /// <summary>Lock on this instance while reading it from another thread.</summary>
    public RingBuffer<HistorySample> History { get; } = new(HistoryLength);
    public WheaTracker Whea { get; }
    public EventJournal Journal { get; }
    public MonitorSnapshot? Last { get; private set; }

    public event Action<MonitorSnapshot>? Updated;

    public void Start()
    {
        _timer ??= new System.Threading.Timer(_ => TimerTick(), null, 0, 1000);
    }

    void TimerTick()
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;   // previous tick still running
        try { Tick(_tickIndex++); }
        catch (Exception) { /* never kill the timer */ }
        finally { Volatile.Write(ref _running, 0); }
    }

    public MonitorSnapshot Tick(int tickIndex)
    {
        MonitorSnapshot snap;
        lock (_tickLock)
        {
            var now = _clock();
            bool refreshTemps = tickIndex % TempRefreshTicks == 0;
            bool slow = tickIndex % SlowRefreshTicks == 0;

            var cpu = Try(_sensors.ReadCpu, CpuReading.Empty);
            _lastDisks = Try(() => _sensors.ReadDisks(refreshTemps), _lastDisks);
            _lastPower = Try(() => _sensors.ReadPower(slow), _lastPower);
            bool esif = Try(() => _sensors.EsifAvailable, false);

            var cpuState = _classifier.Update(cpu);
            if (cpu.T1 is double t1)
            {
                _t1Min = _t1Min is null ? t1 : Math.Min(_t1Min.Value, t1);
                _t1Max = _t1Max is null ? t1 : Math.Max(_t1Max.Value, t1);
            }
            if (cpu.T0 is double t0) _t0PeriodMax = _t0PeriodMax is null ? t0 : Math.Max(_t0PeriodMax.Value, t0);

            bool wheaActive = Whea.SsdWheaActive(now);
            var ssdState = SsdStateClassifier.Classify(_lastDisks, wheaActive);

            Journal.ObserveCpu(now, cpuState, cpu.T1);
            Journal.ObserveSsd(now, ssdState, SsdDetail(ssdState, now));
            string? burstMessage = ObserveBurst(now);

            var sample = new HistorySample(
                (float)(cpu.T1 ?? double.NaN), (float)(cpu.PackageW ?? double.NaN),
                (float)(TempOf("D:") ?? double.NaN), (float)(TempOf("C:") ?? double.NaN));
            lock (History) History.Add(sample);

            if (slow && _log != null) WriteCsv(now, cpu, cpuState);

            snap = new MonitorSnapshot(now, cpu, cpuState, _t1Min, _t1Max, _lastDisks, ssdState, _lastPower, _elevated, esif)
            {
                BurstMessage = burstMessage,
            };
            Last = snap;
        }
        RaiseUpdated(snap);
        return snap;
    }

    /// <summary>
    /// Journals every WHEA burst start (>= 10 events in 60 s, with source and count); returns the
    /// notification text only when the tracker's 10-minute notification gate opens.
    /// </summary>
    string? ObserveBurst(DateTime now)
    {
        int count = Whea.CountLast60s(now);
        if (count < WheaTracker.BurstThreshold) { _inBurst = false; return null; }
        if (_inBurst) return null;
        _inBurst = true;
        var source = BurstSource(now);
        Journal.Add(now, $"WHEA {source}: {count.ToString(CultureInfo.InvariantCulture)} eventi in 1 min");
        return Whea.TryBeginBurst(now, out int n)
            ? $"{n.ToString(CultureInfo.InvariantCulture)} errori WHEA nell'ultimo minuto ({source})"
            : null;
    }

    string BurstSource(DateTime now)
    {
        DiskInfo? last = null; DateTime lastTime = DateTime.MinValue;
        foreach (var d in _lastDisks)
            if (Whea.LastEvent(d.Info.DiskNumber) is DateTime t && t > lastTime) { lastTime = t; last = d.Info; }
        if (last is null || now - lastTime > WheaTracker.BurstWindow) return "altri dispositivi";
        return $"bus {last.Endpoint?.Bus.ToString(CultureInfo.InvariantCulture) ?? "?"} ({last.Letter})";
    }

    void RaiseUpdated(MonitorSnapshot snap)
    {
        var handlers = Updated;
        if (handlers is null) return;
        foreach (Action<MonitorSnapshot> h in handlers.GetInvocationList())
        {
            try { h(snap); } catch (Exception) { }
        }
    }

    static T Try<T>(Func<T> f, T fallback)
    {
        try { return f(); } catch (Exception) { return fallback; }
    }

    static bool SafeIsElevated()
    {
        try { return AppPaths.IsElevated(); } catch (Exception) { return false; }
    }

    DiskReading? DiskByLetter(string letter) => _lastDisks.FirstOrDefault(d => d.Info.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase));
    double? TempOf(string letter) => DiskByLetter(letter)?.TempC;

    string SsdDetail(SsdState s, DateTime now)
    {
        if (s == SsdState.Hot)
        {
            var hot = _lastDisks.Where(d => d.TempC is not null).OrderByDescending(d => d.TempC).FirstOrDefault();
            return hot is null ? "" : $"{hot.Info.Letter} {hot.TempC!.Value.ToString("0", It)} C (avviso {(hot.WarnC is double w ? w.ToString("0", It) : "n/d")})";
        }
        if (s == SsdState.Whea)
        {
            DiskInfo? last = null; DateTime lastTime = DateTime.MinValue;
            foreach (var d in _lastDisks)
                if (Whea.LastEvent(d.Info.DiskNumber) is DateTime t && t > lastTime) { lastTime = t; last = d.Info; }
            if (last is null) return "";
            int today = Whea.CountToday(last.DiskNumber, WheaSide.Endpoint, now) + Whea.CountToday(last.DiskNumber, WheaSide.RootPort, now);
            return $"{last.Letter} bus {last.Endpoint?.Bus.ToString(CultureInfo.InvariantCulture) ?? "?"}, {today} eventi oggi";
        }
        return "";
    }

    void WriteCsv(DateTime now, CpuReading cpu, CpuState state)
    {
        var p = Whea.TakePeriodCounts();
        int Count(string letter, WheaSide side)
            => DiskByLetter(letter) is DiskReading d && p.ByDisk.TryGetValue((d.Info.DiskNumber, side), out var n) ? n : 0;
        double? Zone(string name) => cpu.Zones.FirstOrDefault(z => z.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.TempC;
        var dD = DiskByLetter("D:");
        var dC = DiskByLetter("C:");
        _log!.WriteRow(new CsvRow(now, cpu.T1, _t0PeriodMax, cpu.PackageW, cpu.IaW, cpu.PerfPct, cpu.LoadPct, cpu.LimitPct, state,
            Zone("TZS0"), Zone("TZS1"), dD?.TempC, dC?.TempC, dD?.ReadMBs, dD?.WriteMBs, dC?.ReadMBs, dC?.WriteMBs,
            Count("D:", WheaSide.Endpoint), Count("D:", WheaSide.RootPort), Count("C:", WheaSide.Endpoint), Count("C:", WheaSide.RootPort), p.Other,
            _lastPower.OnAc, _lastPower.BatteryPct, _lastPower.RateW));
        _t0PeriodMax = null;
    }

    void OnJournalAdded(JournalEntry e) => _log?.WriteEvent(e);

    public void Dispose()
    {
        var t = _timer;
        _timer = null;
        if (t != null)
        {
            using var done = new ManualResetEvent(false);
            if (t.Dispose(done)) done.WaitOne(3000);
        }
        if (_log != null) Journal.Added -= OnJournalAdded;
    }
}
