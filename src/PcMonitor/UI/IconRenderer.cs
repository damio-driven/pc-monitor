using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PcMonitor.UI;

public enum IconShape { Square, Chip }

/// <summary>
/// Draws a tray icon (square or chip with cut corners): solid background and white
/// digits from a hand-made 5x8 pixel font (2 px strokes). System fonts at 16 px are
/// either blurry (antialiased) or ragged (1-bit hinted); a pixel font is crisp and
/// centres exactly. Sizes that are multiples of 16 scale the font by an integer.
/// </summary>
public static partial class IconRenderer
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    const int GlyphW = 5, GlyphH = 8;

    // 5x8, 2 px vertical strokes: two digits + 2 px gap = 12 px, so a 16 px icon keeps 2 px side and 4 px top/bottom padding.
    static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = new[] { ".###.", "##.##", "##.##", "##.##", "##.##", "##.##", "##.##", ".###." },
        ['1'] = new[] { ".##..", "###..", ".##..", ".##..", ".##..", ".##..", ".##..", "####." },
        ['2'] = new[] { ".###.", "##.##", "...##", "..##.", ".##..", "##...", "##...", "#####" },
        ['3'] = new[] { ".###.", "##.##", "...##", "..##.", "...##", "...##", "##.##", ".###." },
        ['4'] = new[] { "...##", "..###", ".####", "##.##", "#####", "...##", "...##", "...##" },
        ['5'] = new[] { "#####", "##...", "####.", "...##", "...##", "...##", "##.##", ".###." },
        ['6'] = new[] { ".###.", "##...", "##...", "####.", "##.##", "##.##", "##.##", ".###." },
        ['7'] = new[] { "#####", "...##", "...##", "..##.", "..##.", ".##..", ".##..", ".##.." },
        ['8'] = new[] { ".###.", "##.##", "##.##", ".###.", "##.##", "##.##", "##.##", ".###." },
        ['9'] = new[] { ".###.", "##.##", "##.##", "##.##", ".####", "...##", "...##", ".###." },
        ['-'] = new[] { ".....", ".....", ".....", "#####", "#####", ".....", ".....", "....." },
        ['!'] = new[] { ".##..", ".##..", ".##..", ".##..", ".##..", ".....", ".##..", ".##.." },
    };

    /// <summary>The caller owns the returned icon (one HICON) and must dispose it.</summary>
    public static Icon Render(string text, Color background, int sizePx, IconShape shape = IconShape.Square)
    {
        sizePx = Math.Clamp(sizePx, 16, 256);
        using var bmp = new Bitmap(sizePx, sizePx, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            if (shape == IconShape.Chip)
            {
                // Chip: corners cut at 45 degrees, transparent outside.
                g.Clear(Color.Transparent);
                int s = sizePx, c = Math.Max(3, sizePx / 5);
                using var bg = new SolidBrush(background);
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.FillPolygon(bg, new[] { new Point(c, 0), new Point(s - c, 0), new Point(s, c), new Point(s, s - c),
                                          new Point(s - c, s), new Point(c, s), new Point(0, s - c), new Point(0, c) });
            }
            else
            {
                g.Clear(background);
            }
            DrawText(g, text, sizePx);
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

    static void DrawText(Graphics g, string text, int sizePx)
    {
        var chars = text.Where(Glyphs.ContainsKey).ToArray();
        if (chars.Length == 0) return;
        int scale = Math.Max(1, sizePx / 16);
        int gap = 2;
        int Width() => (chars.Length * GlyphW + (chars.Length - 1) * gap) * scale;
        if (Width() > sizePx) gap = 1;
        while (scale > 1 && Width() > sizePx) scale--;

        int x0 = (sizePx - Width()) / 2;
        int y0 = (sizePx - GlyphH * scale) / 2;
        using var brush = new SolidBrush(Palette.Text);
        for (int i = 0; i < chars.Length; i++)
        {
            var rows = Glyphs[chars[i]];
            int gx = x0 + i * (GlyphW + gap) * scale;
            for (int r = 0; r < GlyphH; r++)
                for (int c = 0; c < GlyphW; c++)
                    if (rows[r][c] == '#') g.FillRectangle(brush, gx + c * scale, y0 + r * scale, scale, scale);
        }
    }
}
