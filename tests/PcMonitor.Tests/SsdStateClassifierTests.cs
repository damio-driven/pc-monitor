using PcMonitor.Logic;
using PcMonitor.Model;

namespace PcMonitor.Tests;

public class SsdStateClassifierTests
{
    static DiskReading D(double? t, double? warn = 86) => new(new DiskInfo(0, "D:", "X", null, null), t, warn, 88, 0, 0, 0);

    [Fact] public void Whea_wins() => Assert.Equal(SsdState.Whea, SsdStateClassifier.Classify(new[] { D(40) }, true));
    [Fact] public void Hot_at_warning_threshold() => Assert.Equal(SsdState.Hot, SsdStateClassifier.Classify(new[] { D(84, warn: 84) }, false));
    [Fact] public void Hot_at_80() => Assert.Equal(SsdState.Hot, SsdStateClassifier.Classify(new[] { D(80) }, false));
    [Fact] public void Warm_at_70() => Assert.Equal(SsdState.Warm, SsdStateClassifier.Classify(new[] { D(55), D(70) }, false));
    [Fact] public void Normal_below_70() => Assert.Equal(SsdState.Normal, SsdStateClassifier.Classify(new[] { D(69) }, false));
    [Fact] public void Unknown_without_temps() => Assert.Equal(SsdState.Unknown, SsdStateClassifier.Classify(new[] { D(null) }, false));
}
