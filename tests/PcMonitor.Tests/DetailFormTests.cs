using PcMonitor.Logic;
using PcMonitor.UI;
using PcMonitor.Whea;

namespace PcMonitor.Tests;

[Collection("Gdi")]
public class DetailFormTests
{
    static void Sta(Action a)
    {
        Exception? error = null;
        var t = new Thread(() => { try { a(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern int GetGuiResources(IntPtr process, int flags);

    /// <summary>Opening and closing the window must give back its GDI and USER handles without waiting for the GC.</summary>
    [Fact] public void Open_close_20_times_does_not_leak_gdi_or_user_handles() => Sta(() =>
    {
        var t = DateTime.Now; using var m = new Monitor(new FakeSensors(), new WheaTracker(t), new EventJournal(), null, () => t);
        m.Tick(0);
        var self = System.Diagnostics.Process.GetCurrentProcess().Handle;
        void Cycle() { var f = new DetailForm(m); f.Show(); Application.DoEvents(); m.Tick(1); Application.DoEvents(); f.Close(); Application.DoEvents(); }
        Cycle();   // warm-up: one-time caches
        int gdi0 = GetGuiResources(self, 0), user0 = GetGuiResources(self, 1);
        for (int i = 0; i < 20; i++) Cycle();
        int gdi = GetGuiResources(self, 0) - gdi0, user = GetGuiResources(self, 1) - user0;
        Assert.True(gdi < 10 && user < 5, $"GDI +{gdi}, USER +{user} after 20 open/close");
    });

    [Fact] public void Fmt_null_is_nd() => Assert.Equal("n/d", DetailForm.Fmt(null, "C"));
    [Fact] public void Fmt_italian() { Assert.Equal("84 C", DetailForm.Fmt(84.4, "C")); Assert.Equal("1,3 MB/s", DetailForm.Fmt(1.25, "MB/s", 1)); }
    [Fact] public void Form_unsubscribes_on_close() => Sta(() =>
    {
        var t = DateTime.Now; using var m = new Monitor(new FakeSensors(), new WheaTracker(t), new EventJournal(), null, () => t);
        var f = new DetailForm(m); f.Show(); f.Close();
        Assert.True(f.IsDisposed); m.Tick(0);   // must not throw ObjectDisposedException
    });
}
