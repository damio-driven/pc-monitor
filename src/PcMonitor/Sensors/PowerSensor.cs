using PcMonitor.Model;
using PcMonitor.Native;

namespace PcMonitor.Sensors;

/// <summary>AC/battery % every call; charge rate only when refreshRate (every 10 s), cached otherwise.</summary>
public static class PowerSensor
{
    static readonly object Lock = new();
    static double? _rateW;

    public static PowerReading Read(bool refreshRate)
    {
        bool? onAc = null;
        int? pct = null;
        if (Battery.GetSystemPowerStatus(out var s))
        {
            onAc = s.ACLineStatus switch { 0 => false, 1 => true, _ => null };
            pct = s.BatteryLifePercent <= 100 ? s.BatteryLifePercent : null;
        }
        lock (Lock)
        {
            if (refreshRate)
            {
                try { _rateW = Battery.QueryRateW(); }
                catch (Exception) { _rateW = null; }
            }
            return new PowerReading(onAc, pct, _rateW);
        }
    }
}
