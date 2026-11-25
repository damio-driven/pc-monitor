using System.Drawing.Drawing2D;
using PcMonitor.Logic;

namespace PcMonitor.UI;

/// <summary>Hand-drawn (GDI+) line chart of the last 10 minutes of history; NaN breaks the line.</summary>
public sealed class ChartControl : Control
{
    IReadOnlyList<(string Label, Color Color, Func<HistorySample, float> Selector)> _series = Array.Empty<(string, Color, Func<HistorySample, float>)>();
    RingBuffer<HistorySample>? _data;
    float _min, _max = 1;

    public ChartControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Palette.PanelBack;
        ForeColor = Palette.MutedText;
    }

    /// <summary>Gridline labels are formatted by this (default: integer).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<float, string> AxisLabel { get; set; } = v => v.ToString("0");

    public void SetSeries(IReadOnlyList<(string Label, Color Color, Func<HistorySample, float> Selector)> series,
                          RingBuffer<HistorySample> data, float min, float max)
    {
        _series = series;
        _data = data;
        _min = min;
        _max = max > min ? max : min + 1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        var plot = new RectangleF(34, 18, Math.Max(10, Width - 42), Math.Max(10, Height - 26));

        using var gridPen = new Pen(Palette.Grid);
        using var textBrush = new SolidBrush(ForeColor);
        for (int i = 0; i <= 4; i++)
        {
            float v = _min + (_max - _min) * i / 4f;
            float y = plot.Bottom - plot.Height * i / 4f;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            g.DrawString(AxisLabel(v), Font, textBrush, 2, y - Font.Height / 2f);
        }

        float lx = plot.Left;
        foreach (var s in _series)
        {
            using var b = new SolidBrush(s.Color);
            g.FillRectangle(b, lx, 4, 10, 10);
            g.DrawString(s.Label, Font, textBrush, lx + 13, 1);
            lx += 13 + g.MeasureString(s.Label, Font).Width + 12;
        }

        if (_data is null) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        lock (_data)
        {
            int count = _data.Count;
            int capacity = _data.Capacity;
            if (count < 2) return;
            float step = plot.Width / (capacity - 1);
            var pts = new List<PointF>(count);
            foreach (var s in _series)
            {
                using var pen = new Pen(s.Color, 1.6f);
                pts.Clear();
                for (int i = 0; i < count; i++)
                {
                    float v = s.Selector(_data[i]);
                    if (float.IsNaN(v))
                    {
                        if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
                        pts.Clear();
                        continue;
                    }
                    float x = plot.Right - (count - 1 - i) * step;
                    float y = plot.Bottom - (Math.Clamp(v, _min, _max) - _min) / (_max - _min) * plot.Height;
                    pts.Add(new PointF(x, y));
                }
                if (pts.Count > 1) g.DrawLines(pen, pts.ToArray());
            }
        }
    }
}
