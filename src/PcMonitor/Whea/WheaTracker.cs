namespace PcMonitor.Whea;

public sealed record WheaPeriodCounts(IReadOnlyDictionary<(int Disk, WheaSide Side), int> ByDisk, int Other);

/// <summary>
/// Aggregated WHEA counters. Fed by the WHEA thread, read by the timer/UI thread.
/// Memory is bounded: per-day and since-boot counters plus a 64-slot per-second
/// ring for the burst window; individual events are never stored.
/// </summary>
public sealed class WheaTracker
{
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan BurstCooldown = TimeSpan.FromMinutes(10);
    public const int BurstThreshold = 10;

    const int SecondSlots = 64;

    readonly object _lock = new();
    readonly Dictionary<(int? Disk, WheaSide Side), int> _sinceBoot = new();
    readonly Dictionary<DateTime, Dictionary<(int? Disk, WheaSide Side), int>> _byDay = new();
    readonly Dictionary<(int Disk, WheaSide Side), int> _period = new();
    readonly Dictionary<int, DateTime> _lastByDisk = new();
    readonly long[] _slotSecond = new long[SecondSlots];
    readonly int[] _slotCount = new int[SecondSlots];
    int _periodOther;
    DateTime? _lastDiskEvent;
    DateTime? _lastBurst;
    DateTime _newestDay = DateTime.MinValue;

    public WheaTracker(DateTime bootTime)
    {
        BootTime = bootTime;
        Array.Fill(_slotSecond, long.MinValue);
    }

    public DateTime BootTime { get; }

    /// <summary>
    /// Records one event. <paramref name="countInPeriod"/> = false for events replayed by the boot
    /// scan: they belong to "since boot" / "today", not to the current CSV period.
    /// </summary>
    public void Add(DateTime time, WheaTarget t, bool countInPeriod = true)
    {
        var key = (t.DiskNumber, t.Side);
        lock (_lock)
        {
            _sinceBoot[key] = _sinceBoot.GetValueOrDefault(key) + 1;

            var day = time.Date;
            if (_newestDay == DateTime.MinValue || day >= _newestDay.AddDays(-1))
            {
                if (!_byDay.TryGetValue(day, out var counts)) _byDay[day] = counts = new();
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (day > _newestDay)
                {
                    _newestDay = day;
                    foreach (var old in _byDay.Keys.Where(d => d < day.AddDays(-1)).ToList()) _byDay.Remove(old);
                }
            }

            if (t.DiskNumber is int disk)
            {
                if (countInPeriod) _period[(disk, t.Side)] = _period.GetValueOrDefault((disk, t.Side)) + 1;
                if (!_lastByDisk.TryGetValue(disk, out var last) || time > last) _lastByDisk[disk] = time;
                if (_lastDiskEvent is null || time > _lastDiskEvent) _lastDiskEvent = time;
            }
            else if (countInPeriod)
            {
                _periodOther++;
            }

            long sec = time.Ticks / TimeSpan.TicksPerSecond;
            int slot = (int)(sec % SecondSlots);
            if (_slotSecond[slot] == sec) _slotCount[slot]++;
            else if (sec > _slotSecond[slot]) { _slotSecond[slot] = sec; _slotCount[slot] = 1; }
        }
    }

    public int CountToday(int? disk, WheaSide side, DateTime now)
    {
        lock (_lock)
            return _byDay.TryGetValue(now.Date, out var c) ? c.GetValueOrDefault((disk, side)) : 0;
    }

    public int CountSinceBoot(int? disk, WheaSide side)
    {
        lock (_lock) return _sinceBoot.GetValueOrDefault((disk, side));
    }

    public DateTime? LastEvent(int disk)
    {
        lock (_lock) return _lastByDisk.TryGetValue(disk, out var t) ? t : null;
    }

    public bool SsdWheaActive(DateTime now)
    {
        lock (_lock) return _lastDiskEvent is DateTime last && now - last <= ActiveWindow;
    }

    /// <summary>Events of any kind in the last 60 s.</summary>
    public int CountLast60s(DateTime now)
    {
        lock (_lock) return CountLast60sLocked(now);
    }

    int CountLast60sLocked(DateTime now)
    {
        long nowSec = now.Ticks / TimeSpan.TicksPerSecond;
        long fromSec = nowSec - (long)BurstWindow.TotalSeconds;
        int n = 0;
        for (int i = 0; i < SecondSlots; i++)
            if (_slotSecond[i] >= fromSec && _slotSecond[i] <= nowSec) n += _slotCount[i];
        return n;
    }

    /// <summary>True at most once per 10 minutes when a burst is under way (notification gate).</summary>
    public bool TryBeginBurst(DateTime now, out int countLast60s)
    {
        lock (_lock)
        {
            int n = CountLast60sLocked(now);
            countLast60s = n;
            if (n < BurstThreshold) return false;
            if (_lastBurst is DateTime lb && now - lb < BurstCooldown) return false;
            _lastBurst = now;
            return true;
        }
    }

    public WheaPeriodCounts TakePeriodCounts()
    {
        lock (_lock)
        {
            var result = new WheaPeriodCounts(new Dictionary<(int, WheaSide), int>(_period), _periodOther);
            _period.Clear();
            _periodOther = 0;
            return result;
        }
    }
}
