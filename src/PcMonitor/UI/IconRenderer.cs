using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PcMonitor.UI;

/// <summary>Draws a square tray icon: solid background, white bold digits sized to fill.</summary>
public static partial class IconRenderer
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    static readonly string FontFamilyName = PickFont();

    static string PickFont()
    {
        // Condensed digits are taller at 16 px; fall back to Segoe UI.
        using var fonts = new InstalledFontCollection();
        foreach (var name in new[] { "Bahnschrift SemiBold Condensed", "Bahnschrift Condensed", "Bahnschrift" })
            if (fonts.Families.Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return name;
        return "Segoe UI";
    }

    /// <summary>The caller owns the returned icon (one HICON) and must dispose it.</summary>
    public static Icon Render(string text, Color background, int sizePx)
    {
        sizePx = Math.Clamp(sizePx, 16, 256);
        using var bmp = new Bitmap(sizePx, sizePx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using var fmt = (StringFormat)StringFormat.GenericTypographic.Clone();
            fmt.Alignment = StringAlignment.Center;
            fmt.LineAlignment = StringAlignment.Center;
            fmt.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip;

            using var font = FitFont(g, text, sizePx, fmt);
            using var brush = new SolidBrush(Palette.Text);
            var rect = new RectangleF(0, 0, sizePx, sizePx + sizePx / 16f);
            g.DrawString(text, font, brush, rect, fmt);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(h);
            return (Icon)temp.Clone();   // the clone owns its own handle
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    static Font FitFont(Graphics g, string text, int sizePx, StringFormat fmt)
    {
        float em = sizePx * 1.05f;
        while (true)
        {
            var f = new Font(FontFamilyName, em, FontStyle.Bold, GraphicsUnit.Pixel);
            var size = g.MeasureString(text, f, int.MaxValue, fmt);
            if ((size.Width <= sizePx && size.Height <= sizePx * 1.25f) || em <= 6) return f;
            f.Dispose();
            em -= 0.5f;
        }
    }
}
