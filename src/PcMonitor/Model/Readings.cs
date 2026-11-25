namespace PcMonitor.Model;

public enum CpuState { Unknown, Normal, Turbo, PowerLimit, Thermal }

public enum SsdState { Unknown, Normal, Warm, Hot, Whea }

public readonly record struct Bdf(int Bus, int Device, int Function)
{
    public override string ToString() => $"{Bus}:{Device}:{Function}";
}

public sealed record ThermalZone(string Name, double TempC, double PassiveLimitPct);

public sealed record CpuReading(
    double? T1, double? T0, double? PackageW, double? IaW,
    double? PerfPct, double? LoadPct, double? LimitPct,
    IReadOnlyList<ThermalZone> Zones)
{
    public static CpuReading Empty { get; } = new(null, null, null, null, null, null, null, Array.Empty<ThermalZone>());
}

public sealed record DiskInfo(int DiskNumber, string Letter, string Model, Bdf? Endpoint, Bdf? RootPort);

public sealed record DiskReading(
    DiskInfo Info, double? TempC, double? WarnC, double? CritC,
    double? ReadMBs, double? WriteMBs, double? LatencyMs);

/// <summary>RateW &gt; 0 charging, &lt; 0 discharging.</summary>
public sealed record PowerReading(bool? OnAc, int? BatteryPct, double? RateW)
{
    public static PowerReading Empty { get; } = new(null, null, null);
}
