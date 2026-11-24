using System.Globalization;
using PcMonitor.Model;
using PcMonitor.Native;

namespace PcMonitor.Sensors;

/// <summary>Disk readings: throughput every call, NVMe temperature only on refresh (every 5 s), cached otherwise.</summary>
public sealed class DiskSensor : IDisposable
{
    readonly IReadOnlyList<DiskInfo> _disks;
    readonly DiskThroughput _throughput = new();
    readonly (double? TempC, double? WarnC, double? CritC)[] _temps;
    bool _tempsRead;

    public DiskSensor(IReadOnlyList<DiskInfo> disks)
    {
        _disks = disks;
        _temps = new (double?, double?, double?)[disks.Count];
    }

    public IReadOnlyList<DiskInfo> Disks => _disks;

    public IReadOnlyList<DiskReading> Read(bool refreshTemps)
    {
        _throughput.Collect();
        if (refreshTemps || !_tempsRead)
        {
            for (int i = 0; i < _disks.Count; i++) _temps[i] = NvmeTemperature.Query(_disks[i].DiskNumber);
            _tempsRead = true;
        }
        var result = new DiskReading[_disks.Count];
        for (int i = 0; i < _disks.Count; i++)
        {
            var (r, w, lat) = _throughput.Get(_disks[i].DiskNumber);
            result[i] = new DiskReading(_disks[i], _temps[i].TempC, _temps[i].WarnC, _temps[i].CritC, r, w, lat);
        }
        return result;
    }

    public void Dispose() => _throughput.Dispose();
}

/// <summary>Per-disk throughput and latency from OS counters (no command reaches the disk).</summary>
public sealed class DiskThroughput : IDisposable
{
    public const string ReadBytes = @"\PhysicalDisk(*)\Disk Read Bytes/sec";
    public const string WriteBytes = @"\PhysicalDisk(*)\Disk Write Bytes/sec";
    public const string SecPerTransfer = @"\PhysicalDisk(*)\Avg. Disk sec/Transfer";

    static readonly IReadOnlyDictionary<string, double> Empty = new Dictionary<string, double>();

    readonly PdhQuery? _query = PdhQuery.TryCreate(ReadBytes, WriteBytes, SecPerTransfer);
    IReadOnlyDictionary<string, double> _read = Empty, _write = Empty, _lat = Empty;

    public bool Collect()
    {
        if (_query?.Collect() != true)
        {
            _read = _write = _lat = Empty;
            return false;
        }
        _read = _query.Values(ReadBytes);
        _write = _query.Values(WriteBytes);
        _lat = _query.Values(SecPerTransfer);
        return true;
    }

    /// <summary>MB/s = 10^6 bytes per second; latency in ms.</summary>
    public (double? ReadMBs, double? WriteMBs, double? LatencyMs) Get(int diskNumber)
        => (Find(_read, diskNumber) / 1_000_000.0, Find(_write, diskNumber) / 1_000_000.0, Find(_lat, diskNumber) * 1000.0);

    /// <summary>PhysicalDisk instance names start with the disk number: "0 D:".</summary>
    static double? Find(IReadOnlyDictionary<string, double> d, int disk)
    {
        var prefix = disk.ToString(CultureInfo.InvariantCulture);
        foreach (var (k, v) in d)
        {
            if (k == prefix || (k.Length > prefix.Length && k.StartsWith(prefix, StringComparison.Ordinal) && k[prefix.Length] == ' '))
                return v;
        }
        return null;
    }

    public void Dispose() => _query?.Dispose();
}
