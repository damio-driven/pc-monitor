using PcMonitor.Model;

namespace PcMonitor.Tests;

sealed class FakeSensors : ISensors
{
    public Func<CpuReading> Cpu = () => new(70, 75, 20, 15, 200, 30, 100, new List<ThermalZone>());
    public bool Throw;
    public CpuReading ReadCpu() => Throw ? throw new InvalidOperationException() : Cpu();
    public IReadOnlyList<DiskReading> ReadDisks(bool r) => new[] { new DiskReading(new DiskInfo(0, "D:", "CT1000T500SSD8", new Bdf(1, 0, 0), new Bdf(0, 6, 0)), 50, 86, 88, 0, 0, 0) };
    public PowerReading ReadPower(bool r) => new(true, 66, 26);
}
