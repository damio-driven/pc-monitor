using PcMonitor.Logic;
using PcMonitor.Model;

namespace PcMonitor.Tests;

public class JournalTests
{
    [Fact] public void Ring_keeps_last_N_oldest_first()
    {
        var r = new RingBuffer<int>(3); for (int i = 1; i <= 5; i++) r.Add(i);
        Assert.Equal(3, r.Count); Assert.Equal(3, r[0]); Assert.Equal(5, r[2]);
    }
    [Fact] public void Thermal_episode_logged_on_exit_with_peak_and_duration()
    {
        var j = new EventJournal(); var t = new DateTime(2026, 11, 24, 17, 40, 0);
        j.ObserveCpu(t, CpuState.Thermal, 95); j.ObserveCpu(t.AddSeconds(20), CpuState.Thermal, 96);
        j.ObserveCpu(t.AddSeconds(45), CpuState.Turbo, 88);
        Assert.Equal("CPU freno termico, 96 C, durata 45 s", j.Entries[0].Text);
        Assert.Equal(t.AddSeconds(45), j.Entries[0].Time);
    }
    [Fact] public void PowerLimit_episode_logged()
    {
        var j = new EventJournal(); var t = DateTime.Today;
        j.ObserveCpu(t, CpuState.PowerLimit, 80); j.ObserveCpu(t.AddSeconds(12), CpuState.Normal, 70);
        Assert.Equal("CPU limite di potenza, durata 12 s", j.Entries[0].Text);
    }
    [Fact] public void Ssd_entries_only_on_entering()
    {
        var j = new EventJournal(); var t = DateTime.Today;
        j.ObserveSsd(t, SsdState.Whea, "bus 2, 12 eventi"); j.ObserveSsd(t.AddSeconds(1), SsdState.Whea, "x");
        Assert.Single(j.Entries); Assert.Equal("Errori PCIe: bus 2, 12 eventi", j.Entries[0].Text);
    }
    [Fact] public void Capacity_200()
    {
        var j = new EventJournal(); for (int i = 0; i < 250; i++) j.Add(DateTime.Today, i.ToString());
        Assert.Equal(200, j.Entries.Count); Assert.Equal("249", j.Entries[0].Text);
    }
}
