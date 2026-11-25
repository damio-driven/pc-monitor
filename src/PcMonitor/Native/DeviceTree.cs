using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PcMonitor.Native;

/// <summary>Kernel32 file/IOCTL helpers. Disks are always opened with access mask 0 (query only).</summary>
internal static unsafe partial class Kernel32
{
    public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuf, uint inSize, void* outBuf, uint outSize, out uint returned, IntPtr overlapped);

    /// <summary>Opens a device for IOCTL queries only (access mask 0 unless asked otherwise). Null on failure.</summary>
    public static SafeFileHandle? OpenDevice(string path, uint access = 0)
    {
        var h = CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return h;
    }

    /// <summary>Runs an IOCTL with a byte input and a byte output buffer; returns bytes written or -1.</summary>
    public static int Ioctl(SafeFileHandle h, uint code, ReadOnlySpan<byte> input, Span<byte> output)
    {
        fixed (byte* i = input)
        fixed (byte* o = output)
        {
            return DeviceIoControl(h, code, input.Length == 0 ? null : i, (uint)input.Length,
                                   output.Length == 0 ? null : o, (uint)output.Length, out uint ret, IntPtr.Zero)
                ? (int)ret : -1;
        }
    }
}

/// <summary>SetupAPI interface enumeration and CfgMgr32 device-node properties.</summary>
internal static unsafe partial class DeviceTree
{
    const uint DIGCF_PRESENT = 0x2, DIGCF_DEVICEINTERFACE = 0x10;
    const uint CR_SUCCESS = 0;

    public static readonly Guid DiskInterface = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");
    public static readonly Guid BatteryInterface = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");

    static readonly Guid PciBusFmtid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    const uint PID_BusNumber = 23, PID_Address = 30;

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public uint cbSize; public Guid InterfaceClassGuid; public uint Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct DEVPROPKEY { public Guid fmtid; public uint pid; }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial IntPtr SetupDiGetClassDevs(in Guid classGuid, IntPtr enumerator, IntPtr hwnd, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, in Guid guid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, void* detail, uint detailSize, out uint required, ref SP_DEVINFO_DATA devInfo);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")]
    private static partial uint CM_Get_DevNode_Property(uint devInst, in DEVPROPKEY key, out uint type, void* buffer, ref uint size, uint flags);

    /// <summary>Present device interfaces of a class: (device path, devinst).</summary>
    public static List<(string Path, uint DevInst)> EnumerateInterfaces(Guid interfaceGuid)
    {
        var result = new List<(string, uint)>();
        IntPtr set = SetupDiGetClassDevs(interfaceGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;
        try
        {
            byte* buf = stackalloc byte[2048];
            for (uint i = 0; ; i++)
            {
                var data = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)sizeof(SP_DEVICE_INTERFACE_DATA) };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, interfaceGuid, i, ref data)) break;
                var info = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
                // SP_DEVICE_INTERFACE_DETAIL_DATA_W: cbSize is 8 on x64, path starts at offset 4.
                *(uint*)buf = IntPtr.Size == 8 ? 8u : 6u;
                if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buf, 2048, out _, ref info)) continue;
                var path = new string((char*)(buf + 4));
                result.Add((path, info.DevInst));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return result;
    }

    public static uint? Parent(uint devInst)
        => CM_Get_Parent(out uint p, devInst, 0) == CR_SUCCESS ? p : null;

    static uint? GetUInt32(uint devInst, uint pid)
    {
        var key = new DEVPROPKEY { fmtid = PciBusFmtid, pid = pid };
        uint value = 0, size = 4;
        return CM_Get_DevNode_Property(devInst, key, out _, &value, ref size, 0) == CR_SUCCESS && size == 4 ? value : null;
    }

    /// <summary>PCI B:D:F from DEVPKEY_Device_BusNumber and DEVPKEY_Device_Address (device high word, function low word).</summary>
    public static Model.Bdf? GetBdf(uint devInst)
    {
        if (GetUInt32(devInst, PID_BusNumber) is not uint bus || GetUInt32(devInst, PID_Address) is not uint addr) return null;
        return new Model.Bdf((int)bus, (int)(addr >> 16), (int)(addr & 0xFFFF));
    }
}
