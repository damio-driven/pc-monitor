using PcMonitor.Model;
using PcMonitor.Whea;

namespace PcMonitor.Tests;

public class WheaTests
{
    const string It = "Componente: PCI Express Endpoint\r\nBus primario:Dispositivo:Funzione: 0x2:0x0:0x0\r\nBus secondario:Dispositivo:Funzione: 0x0:0x0:0x0";
    const string En = "Primary Bus:Device:Function: 0x0:0x6:0x2\r\nSecondary Bus:Device:Function: 0x0:0x0:0x0";
    static readonly DiskInfo Crucial = new(0, "D:", "CT1000T500SSD8", new Bdf(1, 0, 0), new Bdf(0, 6, 0));
    static readonly DiskInfo Wd = new(1, "C:", "WD PC SN5000S", new Bdf(2, 0, 0), new Bdf(0, 6, 2));

    [Fact] public void Parses_italian() { Assert.True(WheaParser.TryParseBdf(It, out var b)); Assert.Equal(new Bdf(2, 0, 0), b); }
    [Fact] public void Parses_english_root_port() { Assert.True(WheaParser.TryParseBdf(En, out var b)); Assert.Equal(new Bdf(0, 6, 2), b); }
    [Fact] public void No_bdf_in_cpu_event() => Assert.False(WheaParser.TryParseBdf("Componente: Processore\r\nOrigine errore: Machine Check Exception", out _));
    [Fact] public void Attributes_endpoint_rootport_other()
    {
        var a = new WheaAttribution(new[] { Crucial, Wd });
        Assert.Equal(new WheaTarget(1, WheaSide.Endpoint), a.Attribute(new Bdf(2, 0, 0)));
        Assert.Equal(new WheaTarget(0, WheaSide.RootPort), a.Attribute(new Bdf(0, 6, 0)));
        Assert.Equal(new WheaTarget(null, WheaSide.Other), a.Attribute(new Bdf(0, 0x1f, 3)));
        Assert.Equal(new WheaTarget(null, WheaSide.Other), a.Attribute(null));
    }
    [Fact] public void Purple_window_is_10_minutes()
    {
        var t0 = new DateTime(2026, 11, 24, 12, 0, 0); var tr = new WheaTracker(t0.AddHours(-1));
        tr.Add(t0, new WheaTarget(0, WheaSide.Endpoint));
        Assert.True(tr.SsdWheaActive(t0.AddMinutes(10)));
        Assert.False(tr.SsdWheaActive(t0.AddMinutes(10).AddSeconds(1)));
    }
    [Fact] public void Other_events_do_not_turn_purple()
    {
        var t0 = new DateTime(2026, 11, 24, 12, 0, 0); var tr = new WheaTracker(t0);
        tr.Add(t0, new WheaTarget(null, WheaSide.Other));
        Assert.False(tr.SsdWheaActive(t0));
    }
    [Fact] public void Burst_fires_once_per_10_minutes()
    {
        var t0 = new DateTime(2026, 11, 24, 12, 0, 0); var tr = new WheaTracker(t0);
        for (int i = 0; i < 9; i++) tr.Add(t0.AddSeconds(i), new WheaTarget(1, WheaSide.Endpoint));
        Assert.False(tr.TryBeginBurst(t0.AddSeconds(9), out _));
        tr.Add(t0.AddSeconds(9), new WheaTarget(1, WheaSide.Endpoint));
        Assert.True(tr.TryBeginBurst(t0.AddSeconds(10), out var n)); Assert.Equal(10, n);
        for (int i = 0; i < 20; i++) tr.Add(t0.AddMinutes(5).AddSeconds(i), new WheaTarget(1, WheaSide.Endpoint));
        Assert.False(tr.TryBeginBurst(t0.AddMinutes(5).AddSeconds(30), out _));
        for (int i = 0; i < 20; i++) tr.Add(t0.AddMinutes(11).AddSeconds(i), new WheaTarget(1, WheaSide.Endpoint));
        Assert.True(tr.TryBeginBurst(t0.AddMinutes(11).AddSeconds(30), out _));
    }
    [Fact] public void Today_resets_at_midnight_since_boot_does_not()
    {
        var boot = new DateTime(2026, 11, 24, 20, 0, 0); var tr = new WheaTracker(boot);
        tr.Add(new DateTime(2026, 11, 24, 23, 59, 0), new WheaTarget(0, WheaSide.Endpoint));
        Assert.Equal(1, tr.CountToday(0, WheaSide.Endpoint, new DateTime(2026, 11, 24, 23, 59, 30)));
        Assert.Equal(0, tr.CountToday(0, WheaSide.Endpoint, new DateTime(2026, 11, 25, 0, 0, 1)));
        Assert.Equal(1, tr.CountSinceBoot(0, WheaSide.Endpoint));
    }
    [Fact] public void Period_counts_reset_after_take()
    {
        var t0 = new DateTime(2026, 11, 24, 12, 0, 0); var tr = new WheaTracker(t0);
        tr.Add(t0, new WheaTarget(1, WheaSide.RootPort)); tr.Add(t0, new WheaTarget(null, WheaSide.Other));
        var p = tr.TakePeriodCounts();
        Assert.Equal(1, p.ByDisk[(1, WheaSide.RootPort)]); Assert.Equal(1, p.Other);
        Assert.Equal(0, tr.TakePeriodCounts().Other);
    }
    [Fact] public void Storm_of_100k_events_stays_bounded()
    {
        var t0 = new DateTime(2026, 11, 24, 12, 0, 0); var tr = new WheaTracker(t0);
        long before = GC.GetTotalMemory(true);
        for (int i = 0; i < 100_000; i++) tr.Add(t0.AddMilliseconds(i * 20), new WheaTarget(0, WheaSide.Endpoint));
        Assert.True(GC.GetTotalMemory(true) - before < 2_000_000);
        Assert.Equal(100_000, tr.CountSinceBoot(0, WheaSide.Endpoint));
    }
}
