using PcMonitor.Model;
using PcMonitor.Native;

namespace PcMonitor.Sensors;

/// <summary>
/// Disk -> letter, model and PCI slot, read once at startup. Only IOCTL queries with
/// access mask 0; no file on any volume is opened.
/// </summary>
public static class DiskTopology
{
    const int BusTypeSata = 11, BusTypeSas = 10, BusTypeNvme = 17, BusTypeAta = 3, BusTypeRaid = 8;

    public static IReadOnlyList<DiskInfo> Discover()
    {
        // 1) Lowest drive letter per disk number. Only the volume device is opened
        //    (access 0) - never GetDriveType or the root directory, which would touch D:'s file system.
        //    Removable / non-disk volumes are filtered out below by the disk descriptor.
        var letterByDisk = new SortedDictionary<int, string>();
        for (char c = 'C'; c <= 'Z'; c++)
        {
            using var vol = Kernel32.OpenDevice($@"\\.\{c}:");
            if (vol is null) continue;
            if (StorageQuery.VolumeFirstDisk(vol) is int disk && !letterByDisk.ContainsKey(disk))
                letterByDisk[disk] = $"{c}:";
        }

        // 2) Disk number -> device node (for the PCI topology).
        var devInstByDisk = new Dictionary<int, uint>();
        foreach (var (path, devInst) in DeviceTree.EnumerateInterfaces(DeviceTree.DiskInterface))
        {
            using var h = Kernel32.OpenDevice(path);
            if (h is null) continue;
            if (StorageQuery.DeviceNumber(h) is int n) devInstByDisk[n] = devInst;
        }

        var result = new List<DiskInfo>();
        foreach (var (disk, letter) in letterByDisk)
        {
            using var h = Kernel32.OpenDevice(StorageQuery.PhysicalDrivePath(disk));
            if (h is null) continue;
            var (product, busType, removable) = StorageQuery.DeviceDescriptor(h);
            if (removable || busType is not (BusTypeNvme or BusTypeSata or BusTypeSas or BusTypeAta or BusTypeRaid)) continue;

            Bdf? endpoint = null, rootPort = null;
            if (devInstByDisk.TryGetValue(disk, out var diskNode) && DeviceTree.Parent(diskNode) is uint controller)
            {
                endpoint = DeviceTree.GetBdf(controller);
                if (DeviceTree.Parent(controller) is uint port) rootPort = DeviceTree.GetBdf(port);
            }
            result.Add(new DiskInfo(disk, letter, product ?? $"Disco {disk}", endpoint, rootPort));
        }
        return result;
    }
}
