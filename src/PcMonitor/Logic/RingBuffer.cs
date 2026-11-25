namespace PcMonitor.Logic;

/// <summary>Fixed-size history; index 0 = oldest. Not thread-safe: callers lock.</summary>
public sealed class RingBuffer<T> where T : struct
{
    readonly T[] _items;
    int _start;

    public RingBuffer(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;
    public int Count { get; private set; }

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public T this[int i]
    {
        get
        {
            if ((uint)i >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(i));
            return _items[(_start + i) % _items.Length];
        }
    }
}

/// <summary>One history point per second; NaN = missing.</summary>
public struct HistorySample
{
    public float T1;
    public float PackageW;
    public float TempD;
    public float TempC;

    public HistorySample(float t1, float packageW, float tempD, float tempC)
    {
        T1 = t1; PackageW = packageW; TempD = tempD; TempC = tempC;
    }
}
