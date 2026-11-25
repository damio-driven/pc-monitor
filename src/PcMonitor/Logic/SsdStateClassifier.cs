using PcMonitor.Model;

namespace PcMonitor.Logic;

/// <summary>SSD colour/state rules from the spec.</summary>
public static class SsdStateClassifier
{
    public const double HotC = 80;
    public const double WarmC = 70;

    public static SsdState Classify(IReadOnlyList<DiskReading> disks, bool wheaActive)
    {
        if (wheaActive) return SsdState.Whea;
        if (disks.Any(d => d.TempC is double t && (t >= HotC || (d.WarnC is double w && t >= w))))
            return SsdState.Hot;
        if (disks.Any(d => d.TempC >= WarmC)) return SsdState.Warm;
        if (disks.Any(d => d.TempC is not null)) return SsdState.Normal;
        return SsdState.Unknown;
    }
}
