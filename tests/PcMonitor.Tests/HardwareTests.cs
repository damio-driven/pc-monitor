using PcMonitor.Model;
using PcMonitor.Native;
using PcMonitor.Sensors;

namespace PcMonitor.Tests;

/// <summary>Machine tests: values of this ThinkBook (spec / CLAUDE.md). Must pass elevated and not.</summary>
public class HardwareTests
{
    [Fact] public void Discovers_both_ssds_with_slot_mapping()
    {
        var d = DiskTopology.Discover();
        var crucial = d.Single(x => x.Model.Contains("CT1000T500"));
        var wd = d.Single(x => x.Model.Contains("SN5000S"));
        Assert.Equal("D:", crucial.Letter); Assert.Equal("C:", wd.Letter);
        Assert.Equal(new Bdf(1, 0, 0), crucial.Endpoint); Assert.Equal(new Bdf(0, 6, 0), crucial.RootPort);
        Assert.Equal(new Bdf(2, 0, 0), wd.Endpoint); Assert.Equal(new Bdf(0, 6, 2), wd.RootPort);
    }
    [Fact] public void Reads_nvme_temperature_and_thresholds()
    {
        var crucial = DiskTopology.Discover().Single(x => x.Model.Contains("CT1000T500"));
        var (t, warn, crit) = NvmeTemperature.Query(crucial.DiskNumber);
        Assert.InRange(t!.Value, 20, 90); Assert.Equal(86, warn); Assert.Equal(88, crit);
    }
    [Fact] public void Nonexistent_disk_returns_nulls() => Assert.Null(NvmeTemperature.Query(99).TempC);
    [Fact] public void Power_reading_on_ac() { var p = PowerSensor.Read(true); Assert.NotNull(p.OnAc); Assert.InRange(p.BatteryPct!.Value, 0, 100); }
}
