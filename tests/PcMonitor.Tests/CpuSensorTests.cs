using PcMonitor.Sensors;

namespace PcMonitor.Tests;

public class CpuSensorTests
{
    const string P = @"pci\ven_8086&dev_7d03&subsys_381417aa&rev_05\3&11583659&1&20_";

    [Fact] public void Maps_esif_by_suffix()
    {
        var r = CpuSensor.Map(new Dictionary<string, double> { { P + "0", 92 }, { P + "1", 84 }, { P + "2", 84 }, { P + "3", 61 } },
                              new Dictionary<string, double> { { P + "0", 280 }, { P + "1", 209 } }, 215, 40, 100,
                              new Dictionary<string, double> { { @"\_TZ.TZS0", 3382 } }, new Dictionary<string, double> { { @"\_TZ.TZS0", 100 } });
        Assert.Equal(84, r.T1); Assert.Equal(92, r.T0); Assert.Equal(28.0, r.PackageW!.Value, 1); Assert.Equal(20.9, r.IaW!.Value, 1);
        Assert.Equal("TZS0", r.Zones[0].Name); Assert.Equal(65.05, r.Zones[0].TempC, 1);
    }
    [Fact] public void Missing_esif_gives_null_temps()   // non-elevated or standby glitch
    {
        var r = CpuSensor.Map(new Dictionary<string, double>(), new Dictionary<string, double>(), 120, 10, 100, new Dictionary<string, double>(), new Dictionary<string, double>());
        Assert.Null(r.T1); Assert.Null(r.PackageW); Assert.Equal(120, r.PerfPct); Assert.Empty(r.Zones);
    }
    [Fact] public void Zero_temperature_is_missing()
        => Assert.Null(CpuSensor.Map(new Dictionary<string, double> { { P + "1", 0 } }, new Dictionary<string, double>(), null, null, null, new Dictionary<string, double>(), new Dictionary<string, double>()).T1);
    [Fact] public void Base_counters_work_on_this_machine()
    {
        using var s = new CpuSensor(); s.Read(); Thread.Sleep(1100); var r = s.Read();
        Assert.NotNull(r.PerfPct); Assert.NotNull(r.LoadPct);
    }
}
