using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PcMonitor.Native;

/// <summary>IOCTL_STORAGE_QUERY_PROPERTY helpers on \\.\PhysicalDriveN, access mask 0 (no admin, no data access).</summary>
internal static class StorageQuery
{
    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
    public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x2D1080;
    public const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;

    public const int StorageDeviceProperty = 0;
    public const int StorageDeviceTemperatureProperty = 52;

    public static string PhysicalDrivePath(int disk) => $@"\\.\PhysicalDrive{disk.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>STORAGE_PROPERTY_QUERY { PropertyId, QueryType = 0 (standard), AdditionalParameters[1] }.</summary>
    public static int QueryProperty(SafeFileHandle h, int propertyId, Span<byte> output)
    {
        Span<byte> query = stackalloc byte[12];
        query.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(query, propertyId);
        return Kernel32.Ioctl(h, IOCTL_STORAGE_QUERY_PROPERTY, query, output);
    }

    /// <summary>STORAGE_DEVICE_DESCRIPTOR: ProductId offset @16, BusType @28.</summary>
    public static (string? Product, int BusType, bool Removable) DeviceDescriptor(SafeFileHandle h)
    {
        Span<byte> buf = stackalloc byte[1024];
        buf.Clear();
        int n = QueryProperty(h, StorageDeviceProperty, buf);
        if (n < 32) return (null, 0, false);
        bool removable = buf[10] != 0;
        int productOffset = BinaryPrimitives.ReadInt32LittleEndian(buf[16..]);
        int busType = BinaryPrimitives.ReadInt32LittleEndian(buf[28..]);
        return (ReadAnsi(buf, productOffset, n), busType, removable);
    }

    static string? ReadAnsi(ReadOnlySpan<byte> buf, int offset, int length)
    {
        if (offset <= 0 || offset >= length) return null;
        int end = offset;
        while (end < length && buf[end] != 0) end++;
        return Encoding.ASCII.GetString(buf[offset..end]).Trim();
    }

    /// <summary>STORAGE_DEVICE_NUMBER { DeviceType, DeviceNumber, PartitionNumber }.</summary>
    public static int? DeviceNumber(SafeFileHandle h)
    {
        Span<byte> buf = stackalloc byte[12];
        return Kernel32.Ioctl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, default, buf) >= 12
            ? BinaryPrimitives.ReadInt32LittleEndian(buf[4..]) : null;
    }

    /// <summary>VOLUME_DISK_EXTENTS: first extent's DiskNumber (@8).</summary>
    public static int? VolumeFirstDisk(SafeFileHandle h)
    {
        Span<byte> buf = stackalloc byte[256];
        int n = Kernel32.Ioctl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, default, buf);
        if (n < 32 || BinaryPrimitives.ReadUInt32LittleEndian(buf) == 0) return null;
        return BinaryPrimitives.ReadInt32LittleEndian(buf[8..]);
    }
}

/// <summary>NVMe composite temperature and thresholds via StorageDeviceTemperatureProperty (52).</summary>
public static class NvmeTemperature
{
    public static (double? TempC, double? WarnC, double? CritC) Query(int diskNumber)
    {
        try
        {
            using var h = Kernel32.OpenDevice(StorageQuery.PhysicalDrivePath(diskNumber));
            if (h is null) return (null, null, null);
            Span<byte> buf = stackalloc byte[512];
            buf.Clear();
            int n = StorageQuery.QueryProperty(h, StorageQuery.StorageDeviceTemperatureProperty, buf);
            return Parse(buf[..Math.Max(n, 0)]);
        }
        catch (Exception)
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// STORAGE_TEMPERATURE_DATA_DESCRIPTOR: CriticalTemperature int16 @8, WarningTemperature int16 @10,
    /// InfoCount uint16 @12, TemperatureInfo[] @24 (16 bytes each, Temperature int16 @+2). Sensor index 0.
    /// </summary>
    internal static (double? TempC, double? WarnC, double? CritC) Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < 24) return (null, null, null);
        short crit = BinaryPrimitives.ReadInt16LittleEndian(d[8..]);
        short warn = BinaryPrimitives.ReadInt16LittleEndian(d[10..]);
        ushort count = BinaryPrimitives.ReadUInt16LittleEndian(d[12..]);
        double? temp = null;
        if (count > 0 && d.Length >= 40)
        {
            short t = BinaryPrimitives.ReadInt16LittleEndian(d[26..]);
            if (t > -100 && t < 200) temp = t;
        }
        return (temp, warn > 0 ? warn : null, crit > 0 ? crit : null);
    }
}
