using System.Diagnostics.Eventing.Reader;
using PcMonitor.Sensors;
using PcMonitor.Whea;

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
}
