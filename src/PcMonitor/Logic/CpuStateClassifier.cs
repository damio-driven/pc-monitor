using PcMonitor.Model;

namespace PcMonitor.Logic;

/// <summary>CPU colour/state rules from the spec. Call <see cref="Update"/> once per second.</summary>
public sealed class CpuStateClassifier
{
    public const double ThermalT1 = 95;
    public const double ThermalT0 = 100;
    public const double PowerLimitMinLoad = 50;
    public const double PowerLimitMaxT1 = 90;
    public const double PowerLimitPerfRatio = 0.75;
    public const int PowerLimitSeconds = 3;

    int _powerLimitStreak;

    public double MaxPerfSeen { get; private set; }

    public CpuState Update(CpuReading r)
    {
        if (r.PerfPct is double p && p > MaxPerfSeen) MaxPerfSeen = p;

        bool powerCandidate =
            r.T1 is double t1 && t1 < PowerLimitMaxT1 &&
            r.LoadPct is double load && load >= PowerLimitMinLoad &&
            r.PerfPct is double perf && perf < PowerLimitPerfRatio * MaxPerfSeen;
        _powerLimitStreak = powerCandidate ? _powerLimitStreak + 1 : 0;

        if (r.T1 >= ThermalT1 || r.T0 >= ThermalT0 || r.Zones.Any(z => z.PassiveLimitPct < 100))
            return CpuState.Thermal;
        if (_powerLimitStreak >= PowerLimitSeconds)
            return CpuState.PowerLimit;
        if (r.PerfPct > 100)
            return CpuState.Turbo;
        if (r.PerfPct is null && r.T1 is null)
            return CpuState.Unknown;
        return CpuState.Normal;
    }
}
