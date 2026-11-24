using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PcMonitor.UI;

internal static class Draw
{
    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        if (d <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Pill(Graphics g, string text, Font font, Color back, float right, float top)
    {
        var size = g.MeasureString(text, font);
        var r = new RectangleF(right - size.Width - 14, top, size.Width + 14, size.Height + 4);
        using var path = Rounded(r, r.Height / 2);
        using var b = new SolidBrush(back);
        g.FillPath(b, path);
        using var t = new SolidBrush(Palette.Text);
        g.DrawString(text, font, t, r.Left + 7, r.Top + 2);
    }

    public static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
    }
}

/// <summary>Rounded dark panel with a title and an optional coloured state pill.</summary>
internal sealed class Card : Panel
{
    string _title = "", _chip = "";
    Color _chipColor = Palette.Unknown;
    readonly Font _titleFont;

    public Card(string title)
    {
        _title = title;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.WindowBack;
        ForeColor = Palette.WindowText;
        Padding = new Padding(14, 40, 14, 12);
        _titleFont = new Font("Segoe UI Semibold", 11F);
    }

    public void SetChip(string text, Color color)
    {
        if (text == _chip && color == _chipColor) return;
        _chip = text;
        _chipColor = color;
        Invalidate(new Rectangle(Width / 2, 0, Width / 2, Padding.Top));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Draw.Setup(g);
        g.Clear(BackColor);
        using (var path = Draw.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 10))
        using (var b = new SolidBrush(Palette.PanelBack))
        using (var border = new Pen(Palette.Grid))
        {
            g.FillPath(b, path);
            g.DrawPath(border, path);
        }
        using (var t = new SolidBrush(ForeColor)) g.DrawString(_title, _titleFont, t, 12, 9);
        if (_chip.Length > 0) Draw.Pill(g, _chip, Font, _chipColor, Width - 12, 10);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _titleFont.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>One big value with caption and a small note under it.</summary>
internal sealed class Tile : Control
{
    readonly Font _captionFont = new("Segoe UI", 8.5F);
    readonly Font _valueFont = new("Segoe UI Semibold", 20F);
    readonly Font _unitFont = new("Segoe UI", 10F);
    string _caption, _value = "n/d", _unit = "", _note = "";
    Color _valueColor = Palette.WindowText;

    public Tile(string caption)
    {
        _caption = caption;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.PanelBack;
        Margin = new Padding(0, 0, 8, 0);
    }

    public void Set(string value, string unit, string note, Color valueColor)
    {
        if (value == _value && unit == _unit && note == _note && valueColor == _valueColor) return;
        (_value, _unit, _note, _valueColor) = (value, unit, note, valueColor);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Draw.Setup(g);
        g.Clear(BackColor);
        using (var path = Draw.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 8))
        using (var b = new SolidBrush(Palette.TileBack))
            g.FillPath(b, path);
        using var muted = new SolidBrush(Palette.MutedText);
        using var value = new SolidBrush(_valueColor);
        g.DrawString(_caption, _captionFont, muted, 10, 6);
        var vs = g.MeasureString(_value, _valueFont);
        float vy = 6 + _captionFont.Height;
        g.DrawString(_value, _valueFont, value, 8, vy);
        if (_unit.Length > 0 && _value != "n/d")
            g.DrawString(_unit, _unitFont, muted, 8 + vs.Width - 6, vy + vs.Height - _unitFont.Height - 8);
        if (_note.Length > 0)
            g.DrawString(_note, _captionFont, muted, 10, Height - _captionFont.Height - 6);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _captionFont.Dispose(); _valueFont.Dispose(); _unitFont.Dispose(); }
        base.Dispose(disposing);
    }
}

/// <summary>One SSD: letter badge, model and slot, temperature, throughput, WHEA counters.</summary>
internal sealed class DiskRow : Control
{
    readonly Font _badgeFont = new("Segoe UI Semibold", 12F);
    readonly Font _boldFont = new("Segoe UI Semibold", 9.5F);
    readonly Font _small = new("Segoe UI", 8.5F);
    readonly Font _tempFont = new("Segoe UI Semibold", 17F);
    string _letter = "", _model = "", _slot = "", _temp = "n/d", _warn = "", _io = "", _whea = "";
    Color _state = Palette.Unknown, _tempColor = Palette.WindowText;

    public DiskRow()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.PanelBack;
        Height = 70;
        Dock = DockStyle.Top;
    }

    public void Set(string letter, string model, string slot, string temp, string warn, Color tempColor, Color state, string io, string whea)
    {
        var next = (letter, model, slot, temp, warn, tempColor, state, io, whea);
        if (next == (_letter, _model, _slot, _temp, _warn, _tempColor, _state, _io, _whea)) return;
        (_letter, _model, _slot, _temp, _warn, _tempColor, _state, _io, _whea) = next;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Draw.Setup(g);
        g.Clear(BackColor);
        using (var path = Draw.Rounded(new RectangleF(0, 2, Width - 1, Height - 6), 8))
        using (var b = new SolidBrush(Palette.TileBack))
            g.FillPath(b, path);

        // Letter badge in the disk's state colour.
        var badge = new RectangleF(10, (Height - 4) / 2f - 20, 40, 40);
        using (var path = Draw.Rounded(badge, 8))
        using (var b = new SolidBrush(_state))
            g.FillPath(b, path);
        using var white = new SolidBrush(Palette.Text);
        using var text = new SolidBrush(Palette.WindowText);
        using var muted = new SolidBrush(Palette.MutedText);
        using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(_letter, _badgeFont, white, badge, center);

        float x = 62;
        g.DrawString(_model, _boldFont, text, x, 9);
        g.DrawString(_slot, _small, muted, x, 9 + _boldFont.Height + 1);
        g.DrawString(_whea, _small, muted, x, 9 + _boldFont.Height + _small.Height + 3);

        // Right block: temperature and threshold, then throughput.
        float right = Width - 12;
        using var rightFmt = new StringFormat { Alignment = StringAlignment.Far };
        using var tempBrush = new SolidBrush(_tempColor);
        g.DrawString(_temp, _tempFont, tempBrush, new RectangleF(right - 120, 4, 120, _tempFont.Height + 4), rightFmt);
        g.DrawString(_warn, _small, muted, new RectangleF(right - 120, 6 + _tempFont.Height, 120, _small.Height + 2), rightFmt);
        g.DrawString(_io, _small, text, new RectangleF(right - 400, 6 + _tempFont.Height, 270, _small.Height + 2), rightFmt);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _badgeFont.Dispose(); _boldFont.Dispose(); _small.Dispose(); _tempFont.Dispose(); }
        base.Dispose(disposing);
    }
}
