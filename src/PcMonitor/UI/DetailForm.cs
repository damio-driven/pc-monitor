using System.Globalization;
using PcMonitor.Logic;
using PcMonitor.Model;
using PcMonitor.Whea;

namespace PcMonitor.UI;

/// <summary>
/// Detail window (dark, card layout). Updates only while visible; unsubscribes and is
/// disposed on close (the tray creates a new one each time).
/// </summary>
public sealed class DetailForm : Form
{
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");
    const float WattScaleMax = 60, CpuTempMax = 110;
    static readonly Color SeriesCpu = Color.FromArgb(0xFF, 0x8A, 0x65);
    static readonly Color SeriesWatt = Color.FromArgb(0x64, 0xB5, 0xF6);
    static readonly Color SeriesD = Color.FromArgb(0xFF, 0xB7, 0x4D);
    static readonly Color SeriesC = Color.FromArgb(0x4D, 0xB6, 0xAC);

    // Shared for the process lifetime: a new Font/Icon per window would leak one HFONT and
    // one HICON (+2 bitmaps) per open until the finalizer runs.
    static readonly Font FormFont = new("Segoe UI", 9F);
    static readonly Lazy<Icon?> AppIcon = new(() =>
    {
        try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { return null; }
    });

    readonly Monitor _monitor;
    readonly Label _banner, _zones, _power;
    readonly Card _cpuCard, _ssdCard, _bottomCard;
    readonly Tile _tTemp, _tWatt, _tTurbo, _tLoad;
    readonly Panel _diskHost;
    readonly List<DiskRow> _diskRows = new();
    readonly ChartControl _cpuChart, _ssdChart;
    readonly EventsView _events;
    bool _subscribed;
    int _journalCount = -1;
    JournalEntry? _journalTop;

    public DetailForm(Monitor m)
    {
        _monitor = m;
        Text = "PcMonitor";
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        Font = FormFont;
        BackColor = Palette.WindowBack;
        ForeColor = Palette.WindowText;
        ClientSize = new Size(820, 880);
        MinimumSize = new Size(700, 600);
        Padding = new Padding(12);
        DoubleBuffered = true;
        if (AppIcon.Value is { } icon) Icon = icon;

        // --- CPU card: four tiles, platform sensors line, chart.
        _tTemp = new Tile("Temperatura");
        _tWatt = new Tile("Potenza package");
        _tTurbo = new Tile("Turbo");
        _tLoad = new Tile("Carico");
        var tiles = new TableLayoutPanel { Dock = DockStyle.Top, Height = 84, ColumnCount = 4, RowCount = 1, BackColor = Palette.PanelBack, Margin = Padding.Empty };
        for (int i = 0; i < 4; i++) tiles.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        foreach (var t in new[] { _tTemp, _tWatt, _tTurbo, _tLoad }) { t.Dock = DockStyle.Fill; tiles.Controls.Add(t); }
        _tLoad.Margin = Padding.Empty;
        _zones = Muted();
        _cpuChart = new ChartControl { Dock = DockStyle.Top, Height = 150 };
        _cpuCard = new Card("CPU") { Dock = DockStyle.Top, Height = 40 + 84 + 30 + 150 + 12 };
        _cpuCard.Controls.Add(_cpuChart);
        _cpuCard.Controls.Add(_zones);
        _cpuCard.Controls.Add(tiles);

        // --- SSD card: one row per disk, chart.
        _diskHost = new Panel { Dock = DockStyle.Top, BackColor = Palette.PanelBack, Height = 0 };
        _ssdChart = new ChartControl { Dock = DockStyle.Top, Height = 130 };
        _ssdCard = new Card("SSD") { Dock = DockStyle.Top, Height = 40 + 130 + 12 };
        _ssdCard.Controls.Add(_ssdChart);
        _ssdCard.Controls.Add(Spacer(6, Palette.PanelBack));
        _ssdCard.Controls.Add(_diskHost);

        // --- Power and events.
        _power = new Label { Dock = DockStyle.Top, Height = 26, ForeColor = Palette.WindowText, BackColor = Palette.PanelBack, UseMnemonic = false };
        _events = new EventsView { Dock = DockStyle.Fill };
        var eventsTitle = new Label { Dock = DockStyle.Top, Height = 24, Text = "Eventi (sessione)", ForeColor = Palette.MutedText, BackColor = Palette.PanelBack };
        _bottomCard = new Card("Alimentazione") { Dock = DockStyle.Fill };
        _bottomCard.Controls.Add(_events);
        _bottomCard.Controls.Add(eventsTitle);
        _bottomCard.Controls.Add(_power);

        _banner = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 40, Visible = false, UseMnemonic = false,
            BackColor = Palette.Banner, ForeColor = Palette.WindowText, Padding = new Padding(8), TextAlign = ContentAlignment.MiddleLeft,
        };

        // Dock=Top stacks in reverse order of addition: Fill first, bottom-most Top first.
        Controls.Add(_bottomCard);
        Controls.Add(Spacer(10, Palette.WindowBack));
        Controls.Add(_ssdCard);
        Controls.Add(Spacer(10, Palette.WindowBack));
        Controls.Add(_cpuCard);
        Controls.Add(_banner);

        _cpuChart.AxisLabel = v => v.ToString("0", It);
        _cpuChart.SetSeries(new (string, Color, Func<HistorySample, float>)[]
        {
            ("Temperatura C", SeriesCpu, s => s.T1),
            ("Watt (scala 0-60)", SeriesWatt, s => s.PackageW * (CpuTempMax / WattScaleMax)),
        }, _monitor.History, 0, CpuTempMax);
        _ssdChart.SetSeries(new (string, Color, Func<HistorySample, float>)[]
        {
            ("D: C", SeriesD, s => s.TempD),
            ("C: C", SeriesC, s => s.TempC),
        }, _monitor.History, 20, 90);

        PlaceNearTray();
        if (_monitor.Last is { } last) Render(last);
    }

    static Label Muted() => new()
    {
        Dock = DockStyle.Top, Height = 30, ForeColor = Palette.MutedText, BackColor = Palette.PanelBack,
        TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, AutoEllipsis = true,
    };

    static Panel Spacer(int h, Color back) => new() { Dock = DockStyle.Top, Height = h, BackColor = back };

    void PlaceNearTray()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        if (Height > wa.Height - 24) Height = wa.Height - 24;
        Location = new Point(Math.Max(wa.Left, wa.Right - Width - 12), Math.Max(wa.Top, wa.Bottom - Height - 12));
    }

    /// <summary>Null when ESIF works. Elevated but no ESIF = Intel DTT not ready (retried every minute), not a privilege problem.</summary>
    public static string? BannerText(bool elevated, bool esifAvailable)
        => esifAvailable ? null
         : !elevated ? "Temperatura e watt CPU non disponibili: avvia PcMonitor con privilegi elevati (PcMonitor.exe --install)."
         : "Sensori Intel DTT (temperatura e watt CPU) non disponibili: nuovo tentativo ogni minuto.";

    public static string Fmt(double? v, string unit, int decimals = 0)
        => v is double d && double.IsFinite(d)
            // Round half away from zero first: "F" formatting alone rounds 1.25 to "1,2".
            ? Math.Round(d, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), It) + " " + unit
            : "n/d";

    static string Num(double? v) => v is double d && double.IsFinite(d) ? Math.Round(d, MidpointRounding.AwayFromZero).ToString("0", It) : "n/d";

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        try { DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int)); } catch (Exception) { }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible && !_subscribed) { _monitor.Updated += OnUpdated; _subscribed = true; }
        else if (!Visible && _subscribed) { _monitor.Updated -= OnUpdated; _subscribed = false; }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_subscribed) { _monitor.Updated -= OnUpdated; _subscribed = false; }
        base.OnFormClosed(e);
    }

    void OnUpdated(MonitorSnapshot s)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed && Visible) Render(s); }); }
        catch (InvalidOperationException) { }   // handle destroyed between check and post
    }

    static Color CpuTempColor(double? t) => t switch
    {
        >= 95 => Palette.Thermal,
        >= 85 => Palette.PowerLimit,
        _ => Palette.WindowText,
    };

    static Color DiskTempColor(DiskReading d) => d.TempC switch
    {
        double t when t >= SsdStateClassifier.HotC || (d.WarnC is double w && t >= w) => Palette.Thermal,
        >= SsdStateClassifier.WarmC => Palette.PowerLimit,
        _ => Palette.WindowText,
    };

    public static string SsdStateText(SsdState s) => s switch
    {
        SsdState.Normal => "normale",
        SsdState.Warm => "tiepido",
        SsdState.Hot => "caldo",
        SsdState.Whea => "errori PCIe",
        _ => "n/d",
    };

    void Render(MonitorSnapshot s)
    {
        var w = _monitor.Whea;
        var c = s.Cpu;
        var banner = BannerText(s.Elevated, s.EsifAvailable);
        _banner.Visible = banner != null;
        if (banner != null) Set(_banner, banner);

        _cpuCard.SetChip(TrayController.CpuStateText(s.CpuState), Palette.For(s.CpuState));
        _tTemp.Set(Num(c.T1), "C", $"min {Num(s.T1Min)}  ·  max {Num(s.T1Max)}", CpuTempColor(c.T1));
        _tWatt.Set(Num(c.PackageW), "W", $"core {Fmt(c.IaW, "W")}", Palette.WindowText);
        _tTurbo.Set(Num(c.PerfPct), "%", $"limite prestazioni {Fmt(c.LimitPct, "%")}", c.PerfPct > 100 ? SeriesWatt : Palette.WindowText);
        _tLoad.Set(Num(c.LoadPct), "%", "", Palette.WindowText);

        var zones = c.Zones.Count == 0 ? "n/d"
            : string.Join("  ·  ", c.Zones.Select(z => $"{z.Name} {Fmt(z.TempC, "C")}" + (z.PassiveLimitPct < 100 ? $" (limite passivo {Fmt(z.PassiveLimitPct, "%")})" : "")));
        double passive = c.Zones.Count == 0 ? 100 : c.Zones.Min(z => z.PassiveLimitPct);
        Set(_zones, $"Sensori piattaforma: {zones}   ·   limite passivo {Fmt(passive, "%")}   ·   " +
                    $"altri WHEA: {w.CountToday(null, WheaSide.Other, s.Time)} oggi / {w.CountSinceBoot(null, WheaSide.Other)} da avvio");

        _ssdCard.SetChip(SsdStateText(s.SsdState), Palette.For(s.SsdState));
        EnsureDiskRows(s.Disks.Count);
        for (int i = 0; i < s.Disks.Count; i++)
        {
            var d = s.Disks[i];
            int n = d.Info.DiskNumber;
            var last = w.LastEvent(n);
            bool diskWhea = last is DateTime lt && s.Time - lt <= WheaTracker.ActiveWindow;
            var state = SsdStateClassifier.Classify(new[] { d }, diskWhea);
            string slot = $"bus {d.Info.Endpoint?.Bus.ToString(CultureInfo.InvariantCulture) ?? "?"}  ·  endpoint {d.Info.Endpoint?.ToString() ?? "?"}  ·  root port {d.Info.RootPort?.ToString() ?? "?"}";
            string whea = $"WHEA oggi: ep {w.CountToday(n, WheaSide.Endpoint, s.Time)} / rp {w.CountToday(n, WheaSide.RootPort, s.Time)}   ·   " +
                          $"da avvio: ep {w.CountSinceBoot(n, WheaSide.Endpoint)} / rp {w.CountSinceBoot(n, WheaSide.RootPort)}   ·   " +
                          $"ultimo: {(last is DateTime t ? t.ToString(t.Date == s.Time.Date ? "HH:mm:ss" : "dd/MM HH:mm", It) : "-")}";
            string io = $"lett {Fmt(d.ReadMBs, "MB/s", 1)}   scritt {Fmt(d.WriteMBs, "MB/s", 1)}   lat {Fmt(d.LatencyMs, "ms", 1)}";
            _diskRows[i].Set(d.Info.Letter, d.Info.Model, slot, Fmt(d.TempC, "C"), $"avviso {Fmt(d.WarnC, "C")}",
                             DiskTempColor(d), Palette.For(state), io, whea);
        }

        var p = s.Power;
        string ac = p.OnAc switch { true => "Collegato alla rete", false => "A batteria", null => "Alimentazione n/d" };
        string rate = p.RateW switch
        {
            double r when r > 0.05 => $"   ·   in carica {Fmt(r, "W")}",
            double r when r < -0.05 => $"   ·   scarica {Fmt(-r, "W")}",
            _ => "",
        };
        Set(_power, $"{ac}   ·   batteria {(p.BatteryPct is int b ? b.ToString(It) + " %" : "n/d")}{rate}");

        RenderJournal();
        _cpuChart.Invalidate();
        _ssdChart.Invalidate();
    }

    void EnsureDiskRows(int count)
    {
        if (_diskRows.Count == count) return;
        _diskHost.SuspendLayout();
        foreach (var r in _diskRows) { _diskHost.Controls.Remove(r); r.Dispose(); }
        _diskRows.Clear();
        for (int i = 0; i < count; i++) _diskRows.Add(new DiskRow());
        for (int i = count - 1; i >= 0; i--) _diskHost.Controls.Add(_diskRows[i]);   // reverse: Dock=Top
        _diskHost.Height = count * 70;
        _diskHost.ResumeLayout();
        _ssdCard.Height = 40 + _diskHost.Height + 6 + 130 + 12;
    }

    void RenderJournal()
    {
        var entries = _monitor.Journal.Entries;
        var top = entries.Count > 0 ? entries[0] : null;
        if (entries.Count == _journalCount && ReferenceEquals(top, _journalTop)) return;
        _journalCount = entries.Count;
        _journalTop = top;
        _events.SetEntries(entries);
    }

    static void Set(Label l, string text)
    {
        if (l.Text != text) l.Text = text;
    }
}
