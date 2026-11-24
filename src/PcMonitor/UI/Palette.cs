using PcMonitor.Model;

namespace PcMonitor.UI;

/// <summary>State colours (white text on top, readable on light and dark taskbars) and the dark window theme.</summary>
public static class Palette
{
    public static readonly Color Normal = Color.FromArgb(0x2E, 0x7D, 0x32);      // green
    public static readonly Color Turbo = Color.FromArgb(0x15, 0x65, 0xC0);       // blue
    public static readonly Color PowerLimit = Color.FromArgb(0xE6, 0x5C, 0x00);  // orange
    public static readonly Color Thermal = Color.FromArgb(0xC6, 0x28, 0x28);     // red
    public static readonly Color Whea = Color.FromArgb(0x7B, 0x1F, 0xA2);        // purple
    public static readonly Color Unknown = Color.FromArgb(0x61, 0x61, 0x61);     // grey
    public static readonly Color Text = Color.White;

    // Dark theme for the detail window.
    public static readonly Color WindowBack = Color.FromArgb(0x1E, 0x1E, 0x1E);
    public static readonly Color PanelBack = Color.FromArgb(0x26, 0x26, 0x26);
    public static readonly Color WindowText = Color.FromArgb(0xE6, 0xE6, 0xE6);
    public static readonly Color MutedText = Color.FromArgb(0x9E, 0x9E, 0x9E);
    public static readonly Color Grid = Color.FromArgb(0x3A, 0x3A, 0x3A);
    public static readonly Color Banner = Color.FromArgb(0x5D, 0x40, 0x37);

    public static Color For(CpuState s) => s switch
    {
        CpuState.Normal => Normal,
        CpuState.Turbo => Turbo,
        CpuState.PowerLimit => PowerLimit,
        CpuState.Thermal => Thermal,
        _ => Unknown,
    };

    public static Color For(SsdState s) => s switch
    {
        SsdState.Normal => Normal,
        SsdState.Warm => PowerLimit,
        SsdState.Hot => Thermal,
        SsdState.Whea => Whea,
        _ => Unknown,
    };
}
