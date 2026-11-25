using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace PcMonitor.Whea;

/// <summary>
/// WHEA-Logger events from the System log: a one-off scan since Windows boot plus a
/// push watcher (no polling). The watcher resumes from the bookmark of the last
/// processed record, so nothing is lost or counted twice between scan and live
/// events, nor after the watcher is re-created.
/// Threading: the watcher callback never takes <see cref="_lock"/> (disabling a watcher
/// waits for a running callback; doing that under the lock would deadlock). Shared
/// state touched by the callback is volatile; watchers are disposed outside the lock.
/// </summary>
public sealed class WheaSource : IDisposable
{
    const string LogName = "System";
    const string ProviderXPath = "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]";

    readonly WheaAttribution _attribution;
    readonly WheaTracker _tracker;
    readonly object _lock = new();
    EventLogWatcher? _watcher;            // guarded by _lock
    volatile EventBookmark? _bookmark;    // written by scan and callback
    volatile bool _broken;
    volatile bool _disposed;
    volatile bool _scanning;

    public WheaSource(WheaAttribution attribution, WheaTracker tracker)
    {
        _attribution = attribution;
        _tracker = tracker;
    }

    public event Action<DateTime, WheaTarget>? EventArrived;

    /// <summary>
    /// Synchronous; callers run it on a background thread, then call <see cref="Start"/>.
    /// Counts events since <paramref name="bootTime"/>; events older than the scan itself
    /// are history and stay out of the current CSV period.
    /// </summary>
    public void ScanSinceBoot(DateTime bootTime)
    {
        _scanning = true;
        ScanGate?.Wait();   // tests only: hold the scan "in progress"
        var scanStart = DateTime.Now;
        var since = bootTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var xpath = $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='{since}']]]";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName, xpath)) { BatchSize = 256 };
            EventBookmark? last = null;
            for (var e = reader.ReadEvent(); e != null && !_disposed; e = reader.ReadEvent())
            {
                using (e)
                {
                    last = e.Bookmark;
                    if (e.TimeCreated is not DateTime t || t < bootTime) continue;
                    _tracker.Add(t, Attribute(e), countInPeriod: t >= scanStart);
                }
            }
            if (_bookmark is null) _bookmark = last;
        }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }
        finally
        {
            _scanning = false;
        }
    }

    /// <summary>
    /// Starts the live watcher, or re-creates it after an error. Idempotent otherwise.
    /// No-op while the boot scan runs: the watcher must start from the scan's bookmark.
    /// </summary>
    public void Start()
    {
        if (_disposed || _scanning) return;
        EventLogWatcher? old;
        lock (_lock)
        {
            if (_watcher != null && !_broken) return;
            old = _watcher;
            _watcher = null;
        }
        DisposeWatcher(old);   // outside the lock: waits for a running callback

        lock (_lock)
        {
            if (_disposed || _scanning || _watcher != null) return;
            EventLogWatcher? w = null;
            try
            {
                var query = new EventLogQuery(LogName, PathType.LogName, ProviderXPath);
                var bookmark = _bookmark;
                w = bookmark != null ? new EventLogWatcher(query, bookmark, true) : new EventLogWatcher(query);
                w.EventRecordWritten += OnEvent;
                _broken = false;
                w.Enabled = true;
                _watcher = w;
            }
            catch (Exception)
            {
                _broken = true;
                old = w;
            }
        }
        if (_watcher is null) DisposeWatcher(old);
    }

    public bool IsRunning
    {
        get { lock (_lock) return _watcher != null && !_broken; }
    }

    void OnEvent(object? sender, EventRecordWrittenEventArgs args)
    {
        if (args.EventException != null || args.EventRecord is null)
        {
            _broken = true;
            return;
        }
        using var e = args.EventRecord;
        try
        {
            Process(e.TimeCreated ?? DateTime.Now, Message(e), e.Bookmark);
        }
        catch (Exception)
        {
            // Never let a bad record kill the watcher thread.
        }
    }

    /// <summary>Watcher-thread handling of one live event. Never takes the source lock.</summary>
    internal void Process(DateTime time, string? message, EventBookmark? bookmark)
    {
        var target = _attribution.Attribute(WheaParser.TryParseBdf(message, out var bdf) ? bdf : null);
        if (bookmark != null) _bookmark = bookmark;
        _tracker.Add(time, target);
        EventArrived?.Invoke(time, target);
    }

    internal object SyncRoot => _lock;

    /// <summary>Tests only: when set, <see cref="ScanSinceBoot"/> waits on it after marking the scan in progress.</summary>
    internal ManualResetEventSlim? ScanGate { get; set; }

    static string? Message(EventRecord e)
    {
        try { return e.FormatDescription(); } catch (EventLogException) { return null; }
    }

    WheaTarget Attribute(EventRecord e)
        => _attribution.Attribute(WheaParser.TryParseBdf(Message(e), out var bdf) ? bdf : null);

    void DisposeWatcher(EventLogWatcher? w)
    {
        if (w is null) return;
        try
        {
            w.EventRecordWritten -= OnEvent;
            w.Enabled = false;
            w.Dispose();
        }
        catch (Exception) { }
    }

    public void Dispose()
    {
        EventLogWatcher? w;
        lock (_lock)
        {
            _disposed = true;
            w = _watcher;
            _watcher = null;
        }
        DisposeWatcher(w);
    }
}
