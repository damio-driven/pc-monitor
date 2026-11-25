using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace PcMonitor.Native;

internal static partial class Battery
{
    const uint IOCTL_BATTERY_QUERY_TAG = 0x294040;
    const uint IOCTL_BATTERY_QUERY_STATUS = 0x29404C;
    const uint BATTERY_UNKNOWN_RATE = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    static string? _devicePath;
    static bool _searched;

    /// <summary>Charge/discharge rate in W of the first battery (positive = charging), or null.</summary>
    public static double? QueryRateW()
    {
        if (!_searched)
        {
            _searched = true;
            var list = DeviceTree.EnumerateInterfaces(DeviceTree.BatteryInterface);
            _devicePath = list.Count > 0 ? list[0].Path : null;
        }
        if (_devicePath is null) return null;

        using var h = Kernel32.OpenDevice(_devicePath, Kernel32.GENERIC_READ | Kernel32.GENERIC_WRITE);
        if (h is null) return null;

        Span<byte> wait = stackalloc byte[4];
        wait.Clear();
        Span<byte> tagBuf = stackalloc byte[4];
        if (Kernel32.Ioctl(h, IOCTL_BATTERY_QUERY_TAG, wait, tagBuf) < 4) return null;
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(tagBuf);
        if (tag == 0) return null;

        // BATTERY_WAIT_STATUS { BatteryTag, Timeout, PowerState, LowCapacity, HighCapacity }
        Span<byte> ws = stackalloc byte[20];
        ws.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(ws, tag);
        // BATTERY_STATUS { PowerState, Capacity, Voltage, Rate (int32 mW) }
        Span<byte> st = stackalloc byte[16];
        if (Kernel32.Ioctl(h, IOCTL_BATTERY_QUERY_STATUS, ws, st) < 16) return null;
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(st[12..]);
        if (rate == BATTERY_UNKNOWN_RATE) return null;
        return unchecked((int)rate) / 1000.0;
    }
}
