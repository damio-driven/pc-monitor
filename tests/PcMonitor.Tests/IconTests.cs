using System.Diagnostics;
using System.Runtime.InteropServices;
using PcMonitor.Model;
using PcMonitor.UI;

namespace PcMonitor.Tests;

public partial class IconTests
{
    [LibraryImport("user32.dll")]
    private static partial int GetGuiResources(IntPtr process, int flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    static IReadOnlyList<DiskReading> ManyDisks(int n)
        => Enumerable.Range(0, n).Select(i => new DiskReading(new DiskInfo(i, $"{(char)('C' + i)}:", "WD PC SN5000S SDEPMSJ-1T00-1101", null, null), 55, 84, 88, 0, 0, 0)).ToList();

    [Fact] public void Icon_renders_requested_size_and_background()
    {
        using var ico = IconRenderer.Render("84", Palette.Turbo, 16); using var bmp = ico.ToBitmap();
        Assert.Equal(16, bmp.Width); Assert.Equal(Palette.Turbo.ToArgb(), bmp.GetPixel(0, 15).ToArgb());
    }
    [Fact] public void Rendering_1000_icons_does_not_leak_gdi()
    {
        int before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
        for (int i = 0; i < 1000; i++) { using var ico = IconRenderer.Render((i % 100).ToString(), Palette.Normal, 16); DestroyIcon(ico.Handle); }
        Assert.True(GetGuiResources(Process.GetCurrentProcess().Handle, 0) - before < 20);
    }
    [Fact] public void Tooltip_truncated_to_127() => Assert.True(TrayController.FormatSsdTooltip(ManyDisks(10), 12345).Length <= 127);
}
