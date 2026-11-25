using System.Globalization;
using PcMonitor.Model;
using PcMonitor.Native;

namespace PcMonitor.Sensors;

/// <summary>
/// Disk readings: throughput every call (OS counters); NVMe temperature refreshed on request
/// (every 5 s) on a background task per disk. The IOCTL is synchronous and an unresponsive SSD
/// can hold it for a long time, so it never runs on the caller's thread: while a query is in
/// flight no new one starts for that disk, and a temperature older than <c>staleAfter</c> reads n/d.
/// </summary>
public sealed class DiskSensor : IDisposable
{
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromSeconds(30);

    readonly IReadOnlyList<DiskInfo> _disks;
    readonly DiskThroughput? _throughput;
    readonly Func<int, (double? TempC, double? WarnC, double? CritC)> _query;
    readonly TimeSpan _staleAfter;
    readonly object _lock = new();
    readonly (double? TempC, double? WarnC, double? CritC)[] _temps;
    readonly long[] _lastOk;        // Stopwatch timestamp of the last completed query, 0 = never
    readonly Task?[] _pending;

    public DiskSensor(IReadOnlyList<DiskInfo> disks)
        : this(disks, NvmeTemperature.Query, DefaultStaleAfter, new DiskThroughput()) { }

    internal DiskSensor(IReadOnlyList<DiskInfo> disks, Func<int, (double?, double?, double?)> query, TimeSpan staleAfter)
        : this(disks, query, staleAfter, null) { }

    DiskSensor(IReadOnlyList<DiskInfo> disks, Func<int, (double?, double?, double?)> query, TimeSpan staleAfter, DiskThroughput? throughput)
    {
        _disks = disks;
        _query = query;
        _staleAfter = staleAfter;
        _throughput = throughput;
        _temps = new (double?, double?, double?)[disks.Count];
        _lastOk = new long[disks.Count];
        _pending = new Task?[disks.Count];
    }

    public IReadOnlyList<DiskInfo> Disks => _disks;

    public IReadOnlyList<DiskReading> Read(bool refreshTemps)
    {
        _throughput?.Collect();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = new DiskReading[_disks.Count];
        lock (_lock)
        {
            for (int i = 0; i < _disks.Count; i++)
            {
                if ((refreshTemps || _pending[i] is null) && _pending[i] is not { IsCompleted: false })
                {
                    int index = i, disk = _disks[i].DiskNumber;
                    _pending[i] = Task.Run(() => QueryOne(index, disk));
                }
                var t = _temps[i];
                bool fresh = _lastOk[i] != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_lastOk[i], now) <= _staleAfter;
                var (r, w, lat) = _throughput?.Get(_disks[i].DiskNumber) ?? (null, null, null);
                result[i] = new DiskReading(_disks[i], fresh ? t.TempC : null, t.WarnC, t.CritC, r, w, lat);
            }
        }
        return result;
    }

    void QueryOne(int index, int disk)
    {
        (double?, double?, double?) value;
        try { value = _query(disk); }
        catch (Exception) { value = (null, null, null); }
        lock (_lock)
        {
            _temps[index] = value;
            _lastOk[index] = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    public void Dispose() => _throughput?.Dispose();
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
