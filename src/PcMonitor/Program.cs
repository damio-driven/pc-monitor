using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using PcMonitor.Model;
using PcMonitor.Sensors;

namespace PcMonitor;

static partial class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--probe", StringComparison.OrdinalIgnoreCase))
            return Probe();
        return 0;
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetStdHandle(int stdHandle);

    /// <summary>
    /// WinExe has no console. Attach to the parent's console only when stdout is not
    /// already redirected: AttachConsole replaces redirected handles (output to a file
    /// would be lost).
    /// </summary>
    static void AttachParentConsoleIfNeeded()
    {
        var h = GetStdHandle(-11);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) AttachConsole(-1);
    }

    static string F(double? v) => v?.ToString("0.0", CultureInfo.InvariantCulture) ?? "null";

    static int Probe()
    {
        AttachParentConsoleIfNeeded();
        var o = Console.Out;
        o.WriteLine();
        o.WriteLine($"PcMonitor probe  elevated={IsElevated()}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var topology = DiskTopology.Discover();
        using var cpu = new CpuSensor();
        using var disks = new DiskThroughput();
        using var diskSensor = new DiskSensor(topology);
        cpu.Read();
        disks.Collect();
        diskSensor.Read(true);
        Thread.Sleep(1000);
        CpuReading r = cpu.Read();
        disks.Collect();
        var diskReadings = diskSensor.Read(true);
        var power = PowerSensor.Read(true);

        o.WriteLine($"ESIF available: {cpu.EsifAvailable}");
        o.WriteLine($"T1={F(r.T1)} T0={F(r.T0)} PackageW={F(r.PackageW)} IaW={F(r.IaW)}");
        o.WriteLine($"PerfPct={F(r.PerfPct)} LoadPct={F(r.LoadPct)} LimitPct={F(r.LimitPct)}");
        foreach (var z in r.Zones)
            o.WriteLine($"Zone {z.Name}: {F(z.TempC)} C passive={F(z.PassiveLimitPct)}%");
        for (int n = 0; n < 4; n++)
        {
            var (rd, wr, lat) = disks.Get(n);
            if (rd is null && wr is null && lat is null) continue;
            o.WriteLine($"Disk {n}: read={F(rd)} MB/s write={F(wr)} MB/s latency={F(lat)} ms");
        }
        foreach (var d in diskReadings)
            o.WriteLine($"{d.Info.Letter} disk {d.Info.DiskNumber} {d.Info.Model} endpoint={d.Info.Endpoint?.ToString() ?? "null"} rootport={d.Info.RootPort?.ToString() ?? "null"} temp={F(d.TempC)} warn={F(d.WarnC)} crit={F(d.CritC)} read={F(d.ReadMBs)} write={F(d.WriteMBs)} lat={F(d.LatencyMs)}");
        o.WriteLine($"Power: onAc={power.OnAc?.ToString() ?? "null"} battery={power.BatteryPct?.ToString(CultureInfo.InvariantCulture) ?? "null"}% rate={F(power.RateW)} W");
        o.Flush();
        return 0;
    }
}
