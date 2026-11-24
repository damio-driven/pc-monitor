using PcMonitor.Logic;
using PcMonitor.Model;
using PcMonitor.Whea;

namespace PcMonitor.Tests;

public class MonitorTests
{
    [Fact] public void Tick_builds_snapshot_and_history()
    {
        var t = new DateTime(2026, 11, 24, 18, 0, 0);
        using var m = new Monitor(new FakeSensors(), new WheaTracker(t), new EventJournal(), null, () => t);
        var s = m.Tick(0);
        Assert.Equal(CpuState.Turbo, s.CpuState); Assert.Equal(SsdState.Normal, s.SsdState); Assert.Equal(1, m.History.Count);
    }
    [Fact] public void Tick_survives_throwing_sensor()
    {
        var f = new FakeSensors { Throw = true }; var t = DateTime.Now;
        using var m = new Monitor(f, new WheaTracker(t), new EventJournal(), null, () => t);
        var s = m.Tick(1);
        Assert.Null(s.Cpu.T1); Assert.Equal(SsdState.Normal, s.SsdState);
    }
    [Fact] public void Whea_turns_snapshot_purple()
    {
        var t = DateTime.Now; var w = new WheaTracker(t); w.Add(t, new WheaTarget(0, WheaSide.Endpoint));
        using var m = new Monitor(new FakeSensors(), w, new EventJournal(), null, () => t);
        Assert.Equal(SsdState.Whea, m.Tick(1).SsdState);
    }
}
