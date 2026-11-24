using PcMonitor.Model;

namespace PcMonitor.Whea;

public enum WheaSide { Endpoint, RootPort, Other }

public readonly record struct WheaTarget(int? DiskNumber, WheaSide Side);

/// <summary>Maps a WHEA B:D:F to the disk whose endpoint or upstream root port it is.</summary>
public sealed class WheaAttribution
{
    readonly Dictionary<Bdf, WheaTarget> _map = new();

    public WheaAttribution(IEnumerable<DiskInfo> disks)
    {
        foreach (var d in disks)
        {
            if (d.Endpoint is Bdf ep) _map.TryAdd(ep, new WheaTarget(d.DiskNumber, WheaSide.Endpoint));
        }
        foreach (var d in disks)
        {
            if (d.RootPort is Bdf rp) _map.TryAdd(rp, new WheaTarget(d.DiskNumber, WheaSide.RootPort));
        }
    }

    public WheaTarget Attribute(Bdf? bdf)
        => bdf is Bdf b && _map.TryGetValue(b, out var t) ? t : new WheaTarget(null, WheaSide.Other);
}
