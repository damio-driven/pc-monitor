using System.Diagnostics.Eventing.Reader;
using PcMonitor.Sensors;
using PcMonitor.Whea;

// xUnit1031: blocking waits are deliberate here (a lock is held across the wait; await cannot run inside lock).
#pragma warning disable xUnit1031

namespace PcMonitor.Tests;

public class WheaSourceTests
{
    [Fact] public void Scan_since_boot_matches_event_log_count()
    {
        var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var tracker = new WheaTracker(boot);
        using var src = new WheaSource(new WheaAttribution(DiskTopology.Discover()), tracker);
        src.ScanSinceBoot(boot);
        var q = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]");
        int expected = 0; using (var r = new EventLogReader(q)) for (var e = r.ReadEvent(); e != null; e = r.ReadEvent()) if (e.TimeCreated >= boot) expected++;
        int total = new[] { 0, 1 }.Sum(d => tracker.CountSinceBoot(d, WheaSide.Endpoint) + tracker.CountSinceBoot(d, WheaSide.RootPort)) + tracker.CountSinceBoot(null, WheaSide.Other);
        Assert.Equal(expected, total);
    }

    static DateTime? LastWheaEvent()
    {
        var q = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]") { ReverseDirection = true };
        using var r = new EventLogReader(q);
        using var e = r.ReadEvent();
        return e?.TimeCreated;
    }

    // Review finding 2: replayed history must not appear as a spike in the first CSV period.
    [Fact] public void Scan_does_not_fill_the_csv_period()
    {
        var since = LastWheaEvent() is DateTime last ? last.AddHours(-1) : DateTime.Today.AddDays(-1);
        var tracker = new WheaTracker(since);
        using var src = new WheaSource(new WheaAttribution(DiskTopology.Discover()), tracker);
        src.ScanSinceBoot(since);
        var p = tracker.TakePeriodCounts();
        Assert.Equal(0, p.ByDisk.Values.Sum() + p.Other);
    }

    // Review finding 1: the watcher callback must never wait on the source lock (Dispose/Start hold it
    // while disabling the watcher, which waits for the callback: deadlock on exit during a storm).
    [Fact] public void Live_event_handling_does_not_take_the_source_lock()
    {
        var tracker = new WheaTracker(DateTime.Today);
        using var src = new WheaSource(new WheaAttribution(Array.Empty<PcMonitor.Model.DiskInfo>()), tracker);
        Task handled;
        lock (src.SyncRoot)
        {
            handled = Task.Run(() => src.Process(DateTime.Now, "Bus primario:Dispositivo:Funzione: 0x2:0x0:0x0", null));
            Assert.True(handled.Wait(2000), "Process blocked on the source lock");
        }
        Assert.Equal(1, tracker.CountSinceBoot(null, WheaSide.Other));
    }

    // Review finding 7: while the boot scan runs, Start() must not create a watcher without the
    // scan's bookmark (events would be counted twice).
    [Fact] public void Start_waits_for_the_boot_scan()
    {
        var since = DateTime.Now;
        using var gate = new ManualResetEventSlim(false);
        using var src = new WheaSource(new WheaAttribution(DiskTopology.Discover()), new WheaTracker(since)) { ScanGate = gate };
        var scan = Task.Run(() => src.ScanSinceBoot(since));
        Thread.Sleep(200);
        src.Start();
        Assert.False(src.IsRunning);
        gate.Set();
        scan.Wait();
        src.Start();
        Assert.True(src.IsRunning);
    }
}
