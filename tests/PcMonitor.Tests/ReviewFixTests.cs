using PcMonitor.Logic;
using PcMonitor.Model;
using PcMonitor.UI;
using PcMonitor.Whea;

namespace PcMonitor.Tests;

/// <summary>Regression tests for the final-review findings.</summary>
public class ReviewFixTests
{
    // Finding 2: events replayed by the boot scan must not land in the first CSV period.
    [Fact] public void Historical_events_do_not_count_in_period()
    {
        var t = new DateTime(2026, 11, 24, 12, 0, 0); var tr = new WheaTracker(t.AddHours(-4));
        tr.Add(t.AddHours(-3), new WheaTarget(1, WheaSide.Endpoint), countInPeriod: false);
        tr.Add(t.AddHours(-3), new WheaTarget(null, WheaSide.Other), countInPeriod: false);
        var p = tr.TakePeriodCounts();
        Assert.Empty(p.ByDisk); Assert.Equal(0, p.Other);
        Assert.Equal(1, tr.CountSinceBoot(1, WheaSide.Endpoint));
    }

    // Finding 3: every burst start goes to the journal; the notification keeps its 10-minute cooldown.
    [Fact] public void Burst_is_journaled_every_time_but_notified_once_per_10_minutes()
    {
        var t0 = new DateTime(2026, 11, 24, 12, 0, 0); var w = new WheaTracker(t0.AddHours(-1)); var j = new EventJournal();
        var now = t0;
        using var m = new Monitor(new FakeSensors(), w, j, null, () => now);
        for (int i = 0; i < 12; i++) w.Add(t0, new WheaTarget(0, WheaSide.Endpoint));
        var s1 = m.Tick(1);
        Assert.NotNull(s1.BurstMessage);
        Assert.Contains(j.Entries, e => e.Text == "WHEA bus 1 (D:): 12 eventi in 1 min");
        now = t0.AddSeconds(1); Assert.Null(m.Tick(2).BurstMessage);                     // same burst: nothing new
        Assert.Single(j.Entries, e => e.Text.StartsWith("WHEA"));
        now = t0.AddMinutes(3);  m.Tick(3);                                                // quiet: burst over
        for (int i = 0; i < 15; i++) w.Add(now, new WheaTarget(0, WheaSide.Endpoint));
        now = now.AddSeconds(1); var s4 = m.Tick(4);
        Assert.Null(s4.BurstMessage);                                                      // inside cooldown: no balloon
        Assert.Equal(2, j.Entries.Count(e => e.Text.StartsWith("WHEA bus 1 (D:)")));      // but journaled
    }

    // Finding 5: one implausible % Processor Performance sample must not poison MaxPerfSeen.
    [Fact] public void Implausible_perf_sample_is_ignored_for_max()
    {
        var c = new CpuStateClassifier();
        c.Update(new CpuReading(60, 60, 20, 15, 220, 10, 100, Array.Empty<ThermalZone>()));
        c.Update(new CpuReading(60, 60, 20, 15, 50000, 10, 100, Array.Empty<ThermalZone>()));   // glitch after resume
        Assert.Equal(220, c.MaxPerfSeen);
        CpuState s = CpuState.Unknown;
        for (int i = 0; i < 3; i++) s = c.Update(new CpuReading(80, 80, 20, 15, 200, 60, 100, Array.Empty<ThermalZone>()));
        Assert.Equal(CpuState.Turbo, s);
    }

    // Finding 6: the "run elevated" banner only when not elevated; a different message when elevated but ESIF missing.
    [Fact] public void Banner_text_depends_on_elevation()
    {
        Assert.Null(DetailForm.BannerText(elevated: true, esifAvailable: true));
        Assert.Contains("privilegi elevati", DetailForm.BannerText(elevated: false, esifAvailable: false));
        var t = DetailForm.BannerText(elevated: true, esifAvailable: false)!;
        Assert.DoesNotContain("privilegi elevati", t); Assert.Contains("DTT", t);
    }
}

public class DiskSensorIsolationTests
{
    // Review finding 4: a hung NVMe IOCTL (unresponsive SSD) must not stall the tick; its temperature turns n/d.
    [Fact] public void Hung_temperature_query_does_not_block_reads()
    {
        var disks = new[] { new DiskInfo(0, "D:", "CT1000T500SSD8", new Bdf(1, 0, 0), new Bdf(0, 6, 0)) };
        using var release = new ManualResetEventSlim(false);
        int calls = 0;
        (double?, double?, double?) Hung(int n) { if (Interlocked.Increment(ref calls) > 1) release.Wait(); return (50, 86, 88); }
        using var sensor = new PcMonitor.Sensors.DiskSensor(disks, Hung, staleAfter: TimeSpan.FromMilliseconds(300));

        sensor.Read(true);
        SpinWait.SpinUntil(() => sensor.Read(false)[0].TempC == 50, 2000);
        Assert.Equal(50, sensor.Read(false)[0].TempC);           // first query completed

        var sw = System.Diagnostics.Stopwatch.StartNew();
        sensor.Read(true);                                        // second query hangs
        sensor.Read(true);                                        // must not queue a third one
        Assert.True(sw.ElapsedMilliseconds < 200, $"Read blocked {sw.ElapsedMilliseconds} ms");
        Thread.Sleep(400);
        Assert.Null(sensor.Read(false)[0].TempC);                 // stale -> n/d
        Assert.Equal(2, calls);
        release.Set();
    }
}

public class EsifRetryTests
{
    // Review finding 6: if Intel DTT is not ready when the task starts, ESIF must be retried, not lost for the session.
    [Fact] public void Esif_query_is_retried_until_available()
    {
        int esifCalls = 0;
        PcMonitor.Native.PdhQuery? Factory(string[] paths)
        {
            if (paths.Contains(PcMonitor.Sensors.CpuSensor.EsifTemp))
                return ++esifCalls < 2 ? null : PcMonitor.Native.PdhQuery.TryCreate(@"\Processor Information(_Total)\% Processor Utility");
            return PcMonitor.Native.PdhQuery.TryCreate(paths);
        }
        using var s = new PcMonitor.Sensors.CpuSensor(Factory, retryEveryReads: 3);
        Assert.False(s.EsifAvailable);
        s.Read(); s.Read();
        Assert.False(s.EsifAvailable);
        s.Read();
        Assert.True(s.EsifAvailable);
        Assert.Equal(2, esifCalls);
    }
}
