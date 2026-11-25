using System.Diagnostics;
using System.Runtime.InteropServices;
using PcMonitor.Model;
using PcMonitor.UI;

namespace PcMonitor.Tests;

[Collection("Gdi")]
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
    /// <summary>Bounding box of the white digits: margins left/right and top/bottom differ by at most 1 px.</summary>
    [Theory]
    [InlineData("67", 16)] [InlineData("49", 16)] [InlineData("67", 20)] [InlineData("100", 20)] [InlineData("--", 24)] [InlineData("88", 32)]
    public void Digits_are_centered(string text, int size)
    {
        using var ico = IconRenderer.Render(text, Palette.Turbo, size); using var bmp = ico.ToBitmap();
        int minX = size, maxX = -1, minY = size, maxY = -1;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R > 160 && c.G > 160 && c.B > 200) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            }
        Assert.True(maxX >= 0, "no text pixels");
        int left = minX, right = size - 1 - maxX, top = minY, bottom = size - 1 - maxY;
        Assert.True(Math.Abs(left - right) <= 1, $"horizontal margins {left}/{right}");
        Assert.True(Math.Abs(top - bottom) <= 1, $"vertical margins {top}/{bottom}");
    }
    [Theory] [InlineData("88")] [InlineData("47")] [InlineData("--")]
    public void Digits_keep_padding_at_16px(string text)
    {
        using var ico = IconRenderer.Render(text, Palette.Turbo, 16); using var bmp = ico.ToBitmap();
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                var c = bmp.GetPixel(x, y);
                bool lit = c.R > 160 && c.G > 160 && c.B > 200;
                if (lit) Assert.True(x >= 2 && x <= 13 && y >= 4 && y <= 11, $"text pixel at {x},{y}");
            }
    }
    [Fact] public void Chip_shape_has_transparent_cut_corners()
    {
        using var ico = IconRenderer.Render("67", Palette.Turbo, 20, IconShape.Chip); using var bmp = ico.ToBitmap();
        foreach (var (x, y) in new[] { (0, 0), (19, 0), (0, 19), (19, 19) }) Assert.Equal(0, bmp.GetPixel(x, y).A);
        Assert.Equal(Palette.Turbo.ToArgb(), bmp.GetPixel(10, 0).ToArgb());   // edge middle still filled
        Assert.Equal(Palette.Turbo.ToArgb(), bmp.GetPixel(0, 10).ToArgb());
    }
    [Fact] public void Tooltip_truncated_to_127() => Assert.True(TrayController.FormatSsdTooltip(ManyDisks(10), 12345).Length <= 127);
}
