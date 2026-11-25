using System.Runtime.InteropServices;

namespace PcMonitor.Native;

internal static partial class PdhNative
{
    public const uint ERROR_SUCCESS = 0;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint PDH_CSTATUS_VALID_DATA = 0;
    public const uint PDH_CSTATUS_NEW_DATA = 1;

    [StructLayout(LayoutKind.Explicit)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double doubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM
    {
        public IntPtr szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(IntPtr query);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(IntPtr query);
}

/// <summary>
/// One PDH query with English counter paths. Never throws after creation:
/// errors become false / empty results.
/// </summary>
public sealed class PdhQuery : IDisposable
{
    static readonly IReadOnlyDictionary<string, double> Empty = new Dictionary<string, double>();

    readonly Dictionary<string, (IntPtr Handle, bool Wildcard)> _counters = new(StringComparer.OrdinalIgnoreCase);
    IntPtr _query;
    IntPtr _buffer;
    uint _bufferSize;
    bool _lastCollectOk;

    PdhQuery(IntPtr query) => _query = query;

    /// <summary>Returns null if the query cannot be opened or any counter cannot be added.</summary>
    public static PdhQuery? TryCreate(params string[] englishPaths)
    {
        if (PdhNative.PdhOpenQuery(null, IntPtr.Zero, out var q) != PdhNative.ERROR_SUCCESS) return null;
        var pq = new PdhQuery(q);
        foreach (var path in englishPaths)
        {
            if (PdhNative.PdhAddEnglishCounter(q, path, IntPtr.Zero, out var c) != PdhNative.ERROR_SUCCESS)
            {
                pq.Dispose();
                return null;
            }
            pq._counters[path] = (c, path.Contains("(*)", StringComparison.Ordinal));
        }
        return pq;
    }

    public bool Collect()
    {
        if (_query == IntPtr.Zero) return _lastCollectOk = false;
        return _lastCollectOk = PdhNative.PdhCollectQueryData(_query) == PdhNative.ERROR_SUCCESS;
    }

    /// <summary>Wildcard path: instance -> value. Otherwise key "". Empty on error or invalid data.</summary>
    public IReadOnlyDictionary<string, double> Values(string englishPath)
    {
        if (!_lastCollectOk || !_counters.TryGetValue(englishPath, out var c)) return Empty;
        return c.Wildcard ? ReadArray(c.Handle) : ReadSingle(c.Handle);
    }

    static IReadOnlyDictionary<string, double> ReadSingle(IntPtr counter)
    {
        const uint fmt = PdhNative.PDH_FMT_DOUBLE | PdhNative.PDH_FMT_NOCAP100;
        if (PdhNative.PdhGetFormattedCounterValue(counter, fmt, out _, out var v) != PdhNative.ERROR_SUCCESS) return Empty;
        if (!IsValid(v)) return Empty;
        return new Dictionary<string, double> { [""] = v.doubleValue };
    }

    unsafe IReadOnlyDictionary<string, double> ReadArray(IntPtr counter)
    {
        const uint fmt = PdhNative.PDH_FMT_DOUBLE | PdhNative.PDH_FMT_NOCAP100;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            uint size = _bufferSize;
            uint status = PdhNative.PdhGetFormattedCounterArray(counter, fmt, ref size, out uint count, _buffer);
            if (status == PdhNative.PDH_MORE_DATA)
            {
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _bufferSize = size + 256;
                _buffer = Marshal.AllocHGlobal((int)_bufferSize);
                continue;
            }
            if (status != PdhNative.ERROR_SUCCESS || _buffer == IntPtr.Zero) return Empty;
            var result = new Dictionary<string, double>((int)count, StringComparer.OrdinalIgnoreCase);
            var items = (PdhNative.PDH_FMT_COUNTERVALUE_ITEM*)_buffer;
            for (int i = 0; i < count; i++)
            {
                if (!IsValid(items[i].FmtValue)) continue;
                var name = Marshal.PtrToStringUni(items[i].szName) ?? "";
                result[name] = items[i].FmtValue.doubleValue;
            }
            return result;
        }
        return Empty;
    }

    static bool IsValid(PdhNative.PDH_FMT_COUNTERVALUE v)
        => (v.CStatus == PdhNative.PDH_CSTATUS_VALID_DATA || v.CStatus == PdhNative.PDH_CSTATUS_NEW_DATA)
           && double.IsFinite(v.doubleValue);

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhNative.PdhCloseQuery(_query); _query = IntPtr.Zero; }
        if (_buffer != IntPtr.Zero) { Marshal.FreeHGlobal(_buffer); _buffer = IntPtr.Zero; _bufferSize = 0; }
        _counters.Clear();
        _lastCollectOk = false;
    }
}
