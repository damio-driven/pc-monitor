using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using PcMonitor.Model;
using PcMonitor.Whea;

namespace PcMonitor.UI;

/// <summary>Two tray icons (CPU, SSD), their menu, tooltips and the WHEA burst notification.</summary>
public sealed class TrayController : ApplicationContext
{
    public const int MaxTooltip = 127;
    static readonly CultureInfo It = CultureInfo.GetCultureInfo("it-IT");

    readonly Monitor _monitor;
    readonly WheaSource _whea;
    readonly SynchronizationContext _ui;
    readonly NotifyIcon _cpuIcon;
    readonly NotifyIcon _ssdIcon;
    readonly ContextMenuStrip _menu;
    Icon? _cpuImage, _ssdImage;
    (string Text, Color Color, int Size) _cpuKey, _ssdKey;
    int _iconSize;
    int _updates;
    bool _exiting;

    public TrayController(Monitor m, WheaSource w)
    {
        _monitor = m;
        _whea = w;
        if (SynchronizationContext.Current is not WindowsFormsSynchronizationContext)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        _ui = SynchronizationContext.Current!;
        _iconSize = CurrentIconSize();

        _menu = new ContextMenuStrip();
        _menu.Items.Add("Apri", null, (_, _) => OpenDetail());
        _menu.Items.Add("Apri cartella log", null, (_, _) => OpenLogFolder());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Esci", null, (_, _) => ExitApp());

        _cpuIcon = NewIcon("CPU");
        _ssdIcon = NewIcon("SSD");
        ApplyIcon(_cpuIcon, ref _cpuImage, ref _cpuKey, "--", Palette.Unknown, IconShape.Chip);
        ApplyIcon(_ssdIcon, ref _ssdImage, ref _ssdKey, "--", Palette.Unknown);
        _ssdIcon.Visible = true;   // shown first: Windows lists newer icons to the left, CPU ends up left of SSD
        _cpuIcon.Visible = true;

        _monitor.Updated += OnUpdated;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    NotifyIcon NewIcon(string text)
    {
        var n = new NotifyIcon { Text = text, ContextMenuStrip = _menu };
        n.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) OpenDetail(); };
        return n;
    }

    static int CurrentIconSize() => Math.Max(16, SystemInformation.SmallIconSize.Width);

    void OnDisplayChanged(object? sender, EventArgs e)
        => _ui.Post(_ => { _iconSize = CurrentIconSize(); if (_monitor.Last is { } s) Render(s); }, null);

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) _whea.Start();   // re-create the watcher if it broke during standby
    }

    void OnUpdated(MonitorSnapshot s) => _ui.Post(_ => { if (!_exiting) Render(s); }, null);

    void Render(MonitorSnapshot s)
    {
        ApplyIcon(_cpuIcon, ref _cpuImage, ref _cpuKey, IconText(s.Cpu.T1), Palette.For(s.CpuState), IconShape.Chip);
        double? maxDisk = s.Disks.Where(d => d.TempC is not null).Select(d => d.TempC).DefaultIfEmpty(null).Max();
        ApplyIcon(_ssdIcon, ref _ssdImage, ref _ssdKey, IconText(maxDisk), Palette.For(s.SsdState));

        _cpuIcon.Text = FormatCpuTooltip(s);
        _ssdIcon.Text = FormatSsdTooltip(s.Disks, WheaToday(s));

        if (s.BurstMessage is { } burst)
            _ssdIcon.ShowBalloonTip(10000, "PcMonitor - errori PCIe", burst, ToolTipIcon.Warning);

        if (++_updates % 60 == 0 && !_whea.IsRunning) _whea.Start();
        Updated?.Invoke(s);
    }

    /// <summary>Raised on the UI thread after the icons are refreshed.</summary>
    public event Action<MonitorSnapshot>? Updated;

    int WheaToday(MonitorSnapshot s)
        => s.Disks.Sum(d => _monitor.Whea.CountToday(d.Info.DiskNumber, WheaSide.Endpoint, s.Time)
                          + _monitor.Whea.CountToday(d.Info.DiskNumber, WheaSide.RootPort, s.Time));

    void ApplyIcon(NotifyIcon target, ref Icon? current, ref (string, Color, int) key, string text, Color color, IconShape shape = IconShape.Square)
    {
        var k = (text, color, _iconSize);
        if (current != null && key == k) return;
        var next = IconRenderer.Render(text, color, _iconSize, shape);
        target.Icon = next;
        current?.Dispose();   // destroys the previous HICON
        current = next;
        key = k;
    }

    public static string IconText(double? t)
        => t is not double v ? "--" : v >= 99.5 ? "!!" : Math.Round(v).ToString("0", CultureInfo.InvariantCulture);

    static string N(double? v, string unit = "") => v is double d ? d.ToString("0", It) + unit : "n/d";

    public static string CpuStateText(CpuState s) => s switch
    {
        CpuState.Normal => "normale",
        CpuState.Turbo => "turbo",
        CpuState.PowerLimit => "limite di potenza (stima)",
        CpuState.Thermal => "freno termico",
        _ => "n/d",
    };

    public static string FormatCpuTooltip(MonitorSnapshot s)
        => Truncate($"CPU {N(s.Cpu.T1, " C")} - {N(s.Cpu.PackageW, " W")} - turbo {N(s.Cpu.PerfPct, "%")} - carico {N(s.Cpu.LoadPct, "%")} - {CpuStateText(s.CpuState)}");

    public static string ShortModel(string model)
    {
        if (model.StartsWith("CT", StringComparison.OrdinalIgnoreCase) || model.Contains("Crucial", StringComparison.OrdinalIgnoreCase)) return "Crucial";
        if (model.StartsWith("WD", StringComparison.OrdinalIgnoreCase) || model.Contains("Western", StringComparison.OrdinalIgnoreCase)) return "WD";
        if (model.Contains("Samsung", StringComparison.OrdinalIgnoreCase)) return "Samsung";
        var first = model.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? model;
        return first.Length > 12 ? first[..12] : first;
    }

    public static string FormatSsdTooltip(IReadOnlyList<DiskReading> disks, int wheaToday)
    {
        var parts = disks.Select(d => $"{d.Info.Letter} {ShortModel(d.Info.Model)} {N(d.TempC, " C")}");
        return Truncate(string.Join(" - ", parts) + $" - WHEA oggi: {wheaToday.ToString(CultureInfo.InvariantCulture)}");
    }

    static string Truncate(string s) => s.Length <= MaxTooltip ? s : s[..(MaxTooltip - 3)] + "...";

    DetailForm? _form;

    /// <summary>Opens the detail window, or brings the existing one to front. UI thread only.</summary>
    public void OpenDetail()
    {
        if (_exiting) return;
        if (_form is { IsDisposed: false })
        {
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
            return;
        }
        _form = new DetailForm(_monitor);
        _form.FormClosed += (_, _) => _form = null;
        _form.Show();
        _form.Activate();
    }

    /// <summary>Thread-safe entry point (used by the second-instance signal).</summary>
    public void OpenDetailFromAnyThread() => _ui.Post(_ => OpenDetail(), null);

    static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception) { }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        ExitThread();
    }

    protected override void ExitThreadCore()
    {
        _exiting = true;
        _form?.Close();
        _monitor.Updated -= OnUpdated;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _cpuIcon.Visible = false;
        _ssdIcon.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cpuIcon.Dispose();
            _ssdIcon.Dispose();
            _menu.Dispose();
            _cpuImage?.Dispose();
            _ssdImage?.Dispose();
        }
        base.Dispose(disposing);
    }
}
