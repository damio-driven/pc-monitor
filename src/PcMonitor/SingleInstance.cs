using System.Security.AccessControl;
using System.Security.Principal;

namespace PcMonitor;

/// <summary>
/// One instance per session: named mutex <c>Local\PcMonitor.SingleInstance</c>. A second
/// start sets the named event <c>Local\PcMonitor.Show</c> and exits; the first instance
/// opens its window. The event carries a Medium integrity label so a non-elevated
/// second start (double click) can signal an elevated first instance.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\PcMonitor.SingleInstance";
    public const string ShowEventName = @"Local\PcMonitor.Show";

    readonly Mutex _mutex;
    readonly EventWaitHandle _show;
    RegisteredWaitHandle? _wait;

    SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        _mutex = mutex;
        _show = show;
    }

    /// <summary>Null when another instance already runs (also when its mutex is not accessible to us).</summary>
    public static SingleInstance? TryAcquire()
    {
        Mutex mutex;
        bool created;
        try { mutex = new Mutex(true, MutexName, out created); }
        catch (UnauthorizedAccessException) { return null; }   // owned by an elevated instance
        if (!created) { mutex.Dispose(); return null; }

        EventWaitHandle show;
        try
        {
            var sec = new EventWaitHandleSecurity();
            var me = WindowsIdentity.GetCurrent().User!.Value;
            // SYSTEM, Administrators, current user: full; interactive users: SYNCHRONIZE | EVENT_MODIFY_STATE.
            // Mandatory label Medium, no-write-up: lets a non-elevated process of the same session set it.
            sec.SetSecurityDescriptorSddlForm($"D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;{me})(A;;0x100002;;;IU)S:(ML;;NW;;;ME)");
            show = EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, ShowEventName, out _, sec);
        }
        catch (Exception)
        {
            show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        }
        return new SingleInstance(mutex, show);
    }

    public void OnShowRequested(Action action)
    {
        _wait?.Unregister(null);
        _wait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => action(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Asks the running instance to open its window. Never throws.</summary>
    public static bool SignalShow()
    {
        try
        {
            using var e = EventWaitHandleAcl.OpenExisting(ShowEventName, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize);
            return e.Set();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _show.Dispose();
        try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
