using PcMonitor.Logic;
using PcMonitor.Model;

namespace PcMonitor.Tests;

public class CpuStateClassifierTests
{
    static CpuReading R(double? t1 = 60, double? t0 = 60, double? perf = 100, double? load = 10, IReadOnlyList<ThermalZone>? z = null)
        => new(t1, t0, 20, 15, perf, load, 100, z ?? new List<ThermalZone>());

    [Fact] public void Thermal_when_T1_95() => Assert.Equal(CpuState.Thermal, new CpuStateClassifier().Update(R(t1: 95)));
    [Fact] public void Thermal_when_T0_100() => Assert.Equal(CpuState.Thermal, new CpuStateClassifier().Update(R(t1: 80, t0: 100)));
    [Fact] public void Thermal_when_passive_limit_below_100()
        => Assert.Equal(CpuState.Thermal, new CpuStateClassifier().Update(R(z: new[] { new ThermalZone("TZS0", 60, 99) })));
    [Fact] public void Turbo_when_perf_above_100() => Assert.Equal(CpuState.Turbo, new CpuStateClassifier().Update(R(perf: 215)));
    [Fact] public void Normal_when_perf_100() => Assert.Equal(CpuState.Normal, new CpuStateClassifier().Update(R(perf: 100)));
    [Fact] public void PowerLimit_after_3_consecutive_seconds()
    {
        var c = new CpuStateClassifier();
        c.Update(R(perf: 220));                        // max seen 220 -> threshold 165
        Assert.NotEqual(CpuState.PowerLimit, c.Update(R(t1: 80, perf: 150, load: 60)));
        Assert.NotEqual(CpuState.PowerLimit, c.Update(R(t1: 80, perf: 150, load: 60)));
        Assert.Equal(CpuState.PowerLimit, c.Update(R(t1: 80, perf: 150, load: 60)));
    }
    [Fact] public void PowerLimit_counter_resets_when_condition_breaks()
    {
        var c = new CpuStateClassifier(); c.Update(R(perf: 220));
        c.Update(R(t1: 80, perf: 150, load: 60)); c.Update(R(t1: 80, perf: 150, load: 60));
        c.Update(R(t1: 80, perf: 150, load: 20));        // load drops
        Assert.NotEqual(CpuState.PowerLimit, c.Update(R(t1: 80, perf: 150, load: 60)));
    }
    [Fact] public void No_PowerLimit_without_temperature_shows_turbo()   // non-elevated run
    {
        var c = new CpuStateClassifier(); c.Update(R(t1: null, t0: null, perf: 220));
        for (int i = 0; i < 3; i++) Assert.Equal(CpuState.Turbo, c.Update(R(t1: null, t0: null, perf: 150, load: 60)));
    }
    [Fact] public void Unknown_when_nothing_known() => Assert.Equal(CpuState.Unknown, new CpuStateClassifier().Update(R(t1: null, t0: null, perf: null)));
}
