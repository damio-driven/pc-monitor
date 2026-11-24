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

    [Fact] public void Fmt_null_is_nd() => Assert.Equal("n/d", DetailForm.Fmt(null, "C"));
    [Fact] public void Fmt_italian() { Assert.Equal("84 C", DetailForm.Fmt(84.4, "C")); Assert.Equal("1,3 MB/s", DetailForm.Fmt(1.25, "MB/s", 1)); }
    [Fact] public void Form_unsubscribes_on_close() => Sta(() =>
    {
        var t = DateTime.Now; using var m = new Monitor(new FakeSensors(), new WheaTracker(t), new EventJournal(), null, () => t);
        var f = new DetailForm(m); f.Show(); f.Close();
        Assert.True(f.IsDisposed); m.Tick(0);   // must not throw ObjectDisposedException
    });
}
