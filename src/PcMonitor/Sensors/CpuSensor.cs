using PcMonitor.Model;
using PcMonitor.Native;

namespace PcMonitor.Sensors;

/// <summary>
/// CPU readings from PDH. ESIF (temperature, RAPL) needs admin and lives in its own
/// query; processor and thermal-zone counters are separate queries too, so one
/// missing source never hides the others.
/// </summary>
public sealed class CpuSensor : IDisposable
{
    public const string EsifTemp = @"\EsifDeviceInformation(*)\Temperature";
    public const string EsifRapl = @"\EsifDeviceInformation(*)\RAPL Power";
    public const string Perf = @"\Processor Information(_Total)\% Processor Performance";
    public const string Utility = @"\Processor Information(_Total)\% Processor Utility";
    public const string Limit = @"\Processor Information(_Total)\% Performance Limit";
    public const string TzTemp = @"\Thermal Zone Information(*)\High Precision Temperature";
    public const string TzPassive = @"\Thermal Zone Information(*)\% Passive Limit";

    static readonly IReadOnlyDictionary<string, double> Empty = new Dictionary<string, double>();

    /// <summary>Reads (about one per second) between attempts to create a missing query.</summary>
    public const int DefaultRetryEveryReads = 60;

    readonly Func<string[], PdhQuery?> _factory;
    readonly int _retryEvery;
    PdhQuery? _esif;
    PdhQuery? _proc;
    PdhQuery? _tz;
    int _reads;

    public CpuSensor() : this(PdhQuery.TryCreate, DefaultRetryEveryReads) { }

    /// <summary>
    /// A query that cannot be created (e.g. Intel DTT not started yet when the logon task runs)
    /// is retried every <paramref name="retryEveryReads"/> reads instead of staying missing all session.
    /// </summary>
    internal CpuSensor(Func<string[], PdhQuery?> factory, int retryEveryReads)
    {
        _factory = factory;
        _retryEvery = Math.Max(1, retryEveryReads);
        _esif = _factory(new[] { EsifTemp, EsifRapl });
        _proc = _factory(new[] { Perf, Utility, Limit });
        _tz = _factory(new[] { TzTemp, TzPassive });
    }

    public bool EsifAvailable => _esif != null;

    void RetryMissing()
    {
        if (++_reads % _retryEvery != 0) return;
        _esif ??= _factory(new[] { EsifTemp, EsifRapl });
        _proc ??= _factory(new[] { Perf, Utility, Limit });
        _tz ??= _factory(new[] { TzTemp, TzPassive });
    }

    /// <summary>Rate counters need two collects: the first call may return nulls.</summary>
    public CpuReading Read()
    {
        RetryMissing();
        bool esifOk = _esif?.Collect() == true;
        bool procOk = _proc?.Collect() == true;
        bool tzOk = _tz?.Collect() == true;
        return Map(
            esifOk ? _esif!.Values(EsifTemp) : Empty,
            esifOk ? _esif!.Values(EsifRapl) : Empty,
            procOk ? Single(_proc!.Values(Perf)) : null,
            procOk ? Single(_proc!.Values(Utility)) : null,
            procOk ? Single(_proc!.Values(Limit)) : null,
            tzOk ? _tz!.Values(TzTemp) : Empty,
            tzOk ? _tz!.Values(TzPassive) : Empty);
    }

    static double? Single(IReadOnlyDictionary<string, double> d) => d.TryGetValue("", out var v) ? v : null;

    public static CpuReading Map(
        IReadOnlyDictionary<string, double> esifTemp, IReadOnlyDictionary<string, double> esifRapl,
        double? perf, double? load, double? limit,
        IReadOnlyDictionary<string, double> tzTemp, IReadOnlyDictionary<string, double> tzPassive)
    {
        double? t1 = BySuffix(esifTemp, "_1");
        double? t0 = BySuffix(esifTemp, "_0");
        if (t1 is <= 0) t1 = null;
        if (t0 is <= 0) t0 = null;
        double? pkg = BySuffix(esifRapl, "_0") / 10.0;
        double? ia = BySuffix(esifRapl, "_1") / 10.0;

        var zones = new List<ThermalZone>(tzTemp.Count);
        foreach (var (instance, raw) in tzTemp.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (instance.Equals("_Total", StringComparison.OrdinalIgnoreCase) || raw <= 0) continue;
            int dot = instance.LastIndexOf('.');
            var name = dot >= 0 ? instance[(dot + 1)..] : instance;
            double passive = tzPassive.TryGetValue(instance, out var p) ? p : 100;
            // Decikelvin -> C in one division (raw/10 - 273.15 rounds twice).
            zones.Add(new ThermalZone(name, (raw - 2731.5) / 10.0, passive));
        }
        return new CpuReading(t1, t0, pkg, ia, perf, load, limit, zones);
    }

    static double? BySuffix(IReadOnlyDictionary<string, double> d, string suffix)
    {
        foreach (var (k, v) in d)
            if (k.EndsWith(suffix, StringComparison.Ordinal)) return v;
        return null;
    }

    public void Dispose()
    {
        _esif?.Dispose();
        _proc?.Dispose();
        _tz?.Dispose();
    }
}
