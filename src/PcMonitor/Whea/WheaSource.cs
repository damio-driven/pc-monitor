using System.Diagnostics.Eventing.Reader;
using System.Globalization;

namespace PcMonitor.Whea;

/// <summary>
/// WHEA-Logger events from the System log: a one-off scan since Windows boot plus a
/// push watcher (no polling). The watcher resumes from the bookmark of the last
/// processed record, so nothing is lost or counted twice between scan and live
/// events, nor after the watcher is re-created.
/// </summary>
public sealed class WheaSource : IDisposable
{
    const string LogName = "System";
    const string ProviderXPath = "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]";

    readonly WheaAttribution _attribution;
    readonly WheaTracker _tracker;
    readonly object _lock = new();
    EventLogWatcher? _watcher;
    EventBookmark? _bookmark;
    bool _broken;
    bool _disposed;

    public WheaSource(WheaAttribution attribution, WheaTracker tracker)
    {
        _attribution = attribution;
        _tracker = tracker;
    }

    public event Action<DateTime, WheaTarget>? EventArrived;

    /// <summary>Synchronous; callers run it on a background thread. Counts events since <paramref name="bootTime"/>.</summary>
    public void ScanSinceBoot(DateTime bootTime)
    {
        var since = bootTime.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var xpath = $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='{since}']]]";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery(LogName, PathType.LogName, xpath)) { BatchSize = 256 };
            EventBookmark? last = null;
            for (var e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
            {
                using (e)
                {
                    last = e.Bookmark;
                    if (e.TimeCreated is not DateTime t || t < bootTime) continue;
                    _tracker.Add(t, Attribute(e));
                }
            }
            lock (_lock) _bookmark ??= last;
        }
        catch (EventLogException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Starts the live watcher, or re-creates it after an error. Idempotent otherwise.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || (_watcher != null && !_broken)) return;
            DisposeWatcher();
            try
            {
                var query = new EventLogQuery(LogName, PathType.LogName, ProviderXPath);
                _watcher = _bookmark != null ? new EventLogWatcher(query, _bookmark, true) : new EventLogWatcher(query);
                _watcher.EventRecordWritten += OnEvent;
                _watcher.Enabled = true;
                _broken = false;
            }
            catch (Exception)
            {
                DisposeWatcher();
                _broken = true;
            }
        }
    }

    public bool IsRunning
    {
        get { lock (_lock) return _watcher != null && !_broken; }
    }

    void OnEvent(object? sender, EventRecordWrittenEventArgs args)
    {
        if (args.EventException != null || args.EventRecord is null)
        {
            lock (_lock) _broken = true;
            return;
        }
        using var e = args.EventRecord;
        try
        {
            var t = e.TimeCreated ?? DateTime.Now;
            var target = Attribute(e);
            lock (_lock) _bookmark = e.Bookmark;
            _tracker.Add(t, target);
            EventArrived?.Invoke(t, target);
        }
        catch (Exception)
        {
            // Never let a bad record kill the watcher thread.
        }
    }

    WheaTarget Attribute(EventRecord e)
    {
        string? message = null;
        try { message = e.FormatDescription(); } catch (EventLogException) { }
        return _attribution.Attribute(WheaParser.TryParseBdf(message, out var bdf) ? bdf : null);
    }

    void DisposeWatcher()
    {
        if (_watcher is null) return;
        try
        {
            _watcher.EventRecordWritten -= OnEvent;
            _watcher.Enabled = false;
            _watcher.Dispose();
        }
        catch (Exception) { }
        _watcher = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            DisposeWatcher();
        }
    }
}
