# PcMonitor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lightweight Windows tray app with two icons (CPU, SSD) and a detail window, so the user can keep HWiNFO closed.

**Architecture:** WinForms exe. Pure logic (classifiers, WHEA attribution/tracking, journal, ring buffer, CSV) is isolated and unit-tested. Native readers (PDH, NVMe IOCTL, battery IOCTL, CfgMgr32 topology, EventLog) sit behind small classes verified on this machine with a `--probe` mode. A `Monitor` orchestrator polls on a background timer and raises snapshots to the UI thread.

**Tech Stack:** .NET 10 (`net10.0-windows`), WinForms, P/Invoke (pdh.dll, kernel32, setupapi, cfgmgr32), `System.Diagnostics.EventLog` NuGet, xUnit.

**Spec:** `docs/superpowers/specs/2026-11-24-pcmonitor-design.md`

**Repo:** `C:\Git\pc-monitor` (git repo created in Task 1, Step 0). Project notes and hard constraints: `CLAUDE.md` at the repo root.

## Global Constraints

- No kernel driver of any kind. No `System.Management` (WMI) package.
- No reads or writes of the D: volume's files. SSD access = `IOCTL_STORAGE_QUERY_PROPERTY` with access mask 0 only.
- PDH counters added with `PdhAddEnglishCounterW` only (Windows UI is Italian).
- Polling: PDH + `GetSystemPowerStatus` every 1 s; NVMe temperature every 5 s; battery rate + CSV row every 10 s; WHEA push via `EventLogWatcher`.
- History 600 samples (10 min). Journal keeps last 200 entries.
- Logs in `%LOCALAPPDATA%\PcMonitor\logs\`, files `pcmonitor-yyyyMMdd.csv` and `events-yyyyMMdd.log`, kept 7 days, flushed every line.
- CSV: separator `;`, numbers formatted with `it-IT` culture (comma decimals), missing value = empty field.
- UI language Italian, dark theme. Missing value shown as `n/d` (window) and `--` (icon).
- Target footprint: < 1% average CPU, ~40 MB private bytes with window closed. Window is disposed on close.
- Errors in one source never stop other sources or the app.
- Install path `C:\Eseguibili\_\PcMonitor\PcMonitor.exe` with a protected ACL (changed from `%LOCALAPPDATA%\PcMonitor\` after the final security review, user decision 25/11/2026); scheduled task name `PcMonitor`, at logon, highest privileges, 30 s delay.
- Publish: framework-dependent, `PublishSingleFile=true`, win-x64.

## Review Focus

1. **Resume from Modern Standby:** PDH collect returns errors or garbage for a few seconds → readings become `null`, app keeps running, values return; `EventLogWatcher` still fires afterwards. (Task 5 test: `CpuSensor` maps PDH failure to null fields; Task 8 test: monitor tick survives a throwing sensor.)
2. **GDI handle leak from per-second icon redraw:** each new `HICON` must be destroyed when replaced; handle count must stay flat over hours. (Task 8 test: render 1000 icons, GDI object count grows < 20.)
3. **Tray tooltip longer than 127 chars** throws in `NotifyIcon.Text` → truncate to 127. (Task 8 test.)
4. **Started without elevation:** ESIF query fails → T1/T0/W are null, CPU icon shows `--` with Turbo/Normal colour from perf %, window shows the "avvia con privilegi" banner, no crash. (Task 1 classifier test with null temps; Task 5 test.)
5. **WHEA storm (thousands/min) and midnight rollover:** tracker memory stays bounded, UI thread not blocked, "oggi" counters and CSV file switch at local midnight. (Task 2 tests: 100 000 events bounded, midnight reset; Task 4 test: file name changes after midnight.)

---

## File Structure

```
PcMonitor.sln
src/PcMonitor/PcMonitor.csproj
src/PcMonitor/Program.cs                    entry, single instance, --install/--uninstall/--probe
src/PcMonitor/Model/Readings.cs             CpuReading, DiskReading, DiskInfo, PowerReading, Bdf, enums
src/PcMonitor/Logic/CpuStateClassifier.cs   CPU colour/state rules
src/PcMonitor/Logic/SsdStateClassifier.cs   SSD colour/state rules
src/PcMonitor/Logic/RingBuffer.cs           fixed-size history
src/PcMonitor/Logic/EventJournal.cs         state-change journal with durations
src/PcMonitor/Whea/WheaParser.cs            message -> Bdf
src/PcMonitor/Whea/WheaAttribution.cs       Bdf -> (disk, side)
src/PcMonitor/Whea/WheaTracker.cs           counts, purple window, bursts, period counts
src/PcMonitor/Whea/WheaSource.cs            EventLogWatcher + boot scan
src/PcMonitor/Logging/CsvLogger.cs          daily CSV + events log + retention
src/PcMonitor/Native/Pdh.cs                 PDH interop + PdhQuery wrapper
src/PcMonitor/Native/Nvme.cs                NVMe temperature IOCTL
src/PcMonitor/Native/Battery.cs             GetSystemPowerStatus + battery IOCTL
src/PcMonitor/Native/DeviceTree.cs          CfgMgr32/SetupAPI helpers
src/PcMonitor/Sensors/CpuSensor.cs
src/PcMonitor/Sensors/DiskSensor.cs
src/PcMonitor/Sensors/DiskTopology.cs
src/PcMonitor/Sensors/PowerSensor.cs
src/PcMonitor/Monitor.cs                    orchestrator, MonitorSnapshot
src/PcMonitor/UI/Palette.cs
src/PcMonitor/UI/IconRenderer.cs
src/PcMonitor/UI/TrayController.cs
src/PcMonitor/UI/DetailForm.cs
src/PcMonitor/UI/ChartControl.cs
src/PcMonitor/Install/TaskInstaller.cs
tests/PcMonitor.Tests/PcMonitor.Tests.csproj
tests/PcMonitor.Tests/*.cs
```

---

### Task 1: Solution scaffold, models, state classifiers

**Files:**
- Create: `PcMonitor.sln`, `src/PcMonitor/PcMonitor.csproj`, `src/PcMonitor/Program.cs` (minimal `Main` returning 0 for now), `src/PcMonitor/Model/Readings.cs`, `src/PcMonitor/Logic/CpuStateClassifier.cs`, `src/PcMonitor/Logic/SsdStateClassifier.cs`, `.gitignore` (bin/ obj/)
- Test: `tests/PcMonitor.Tests/PcMonitor.Tests.csproj`, `tests/PcMonitor.Tests/CpuStateClassifierTests.cs`, `tests/PcMonitor.Tests/SsdStateClassifierTests.cs`

**Interfaces:**
- Produces (`PcMonitor.Model`):
  - `enum CpuState { Unknown, Normal, Turbo, PowerLimit, Thermal }`
  - `enum SsdState { Unknown, Normal, Warm, Hot, Whea }`
  - `readonly record struct Bdf(int Bus, int Device, int Function)` with `ToString()` → `"0:6:2"`
  - `sealed record CpuReading(double? T1, double? T0, double? PackageW, double? IaW, double? PerfPct, double? LoadPct, double? LimitPct, IReadOnlyList<ThermalZone> Zones)`; `sealed record ThermalZone(string Name, double TempC, double PassiveLimitPct)`
  - `sealed record DiskInfo(int DiskNumber, string Letter, string Model, Bdf? Endpoint, Bdf? RootPort)`
  - `sealed record DiskReading(DiskInfo Info, double? TempC, double? WarnC, double? CritC, double? ReadMBs, double? WriteMBs, double? LatencyMs)`
  - `sealed record PowerReading(bool? OnAc, int? BatteryPct, double? RateW)` (RateW > 0 charging, < 0 discharging)
- Produces (`PcMonitor.Logic`):
  - `sealed class CpuStateClassifier { CpuState Update(CpuReading r); double MaxPerfSeen { get; } }` — call once per second.
  - `static class SsdStateClassifier { SsdState Classify(IReadOnlyList<DiskReading> disks, bool wheaActive); }`

Rules (spec, verbatim values), evaluated top-down:
- CPU Thermal: `T1 >= 95` or `T0 >= 100` or any `Zones[i].PassiveLimitPct < 100`.
- CPU PowerLimit: `LoadPct >= 50` and `T1 < 90` and `PerfPct < 0.75 * MaxPerfSeen` for 3 consecutive `Update` calls (the 3rd call returns PowerLimit). When T1 is null this rule does not apply.
- CPU Turbo: `PerfPct > 100`. Normal otherwise. Unknown if PerfPct is null and T1 is null.
- SSD Whea if `wheaActive`; Hot if any disk `TempC >= WarnC` or `TempC >= 80`; Warm if any `TempC >= 70`; Normal if at least one TempC known; else Unknown.

- [ ] **Step 0: Initialize the git repository**

The folder `C:\Git\pc-monitor` is not a git repo yet and contains only `docs/` and `CLAUDE.md`.
```
git init -b main
git add docs CLAUDE.md
git commit -m "docs: PcMonitor design spec, plan and project notes"
```
Expected: `git log --oneline` shows one commit. Use the global git identity already configured (do not override it).

- [ ] **Step 1: Scaffold**

Run from repo root (`C:\Git\pc-monitor`):
```
dotnet new sln -n PcMonitor
dotnet new winforms -n PcMonitor -o src/PcMonitor -f net10.0
dotnet new xunit -n PcMonitor.Tests -o tests/PcMonitor.Tests -f net10.0
dotnet sln add src/PcMonitor tests/PcMonitor.Tests
dotnet add tests/PcMonitor.Tests reference src/PcMonitor
```
Set `TargetFramework` to `net10.0-windows` in both csproj; test csproj also gets `<UseWindowsForms>true</UseWindowsForms>`. Main csproj: `<Nullable>enable</Nullable>`, `<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>`, `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, `<InvariantGlobalization>false</InvariantGlobalization>`. Delete template `Form1.*` and `UnitTest1.cs`.

- [ ] **Step 2: Write failing tests**

```csharp
static CpuReading R(double? t1=60, double? t0=60, double? perf=100, double? load=10, IReadOnlyList<ThermalZone>? z=null)
    => new(t1, t0, 20, 15, perf, load, 100, z ?? new List<ThermalZone>());

[Fact] public void Thermal_when_T1_95() => Assert.Equal(CpuState.Thermal, new CpuStateClassifier().Update(R(t1:95)));
[Fact] public void Thermal_when_T0_100() => Assert.Equal(CpuState.Thermal, new CpuStateClassifier().Update(R(t1:80, t0:100)));
[Fact] public void Thermal_when_passive_limit_below_100()
    => Assert.Equal(CpuState.Thermal, new CpuStateClassifier().Update(R(z: new[]{ new ThermalZone("TZS0", 60, 99) })));
[Fact] public void Turbo_when_perf_above_100() => Assert.Equal(CpuState.Turbo, new CpuStateClassifier().Update(R(perf:215)));
[Fact] public void Normal_when_perf_100() => Assert.Equal(CpuState.Normal, new CpuStateClassifier().Update(R(perf:100)));
[Fact] public void PowerLimit_after_3_consecutive_seconds()
{
    var c = new CpuStateClassifier();
    c.Update(R(perf:220));                        // max seen 220 -> threshold 165
    Assert.NotEqual(CpuState.PowerLimit, c.Update(R(t1:80, perf:150, load:60)));
    Assert.NotEqual(CpuState.PowerLimit, c.Update(R(t1:80, perf:150, load:60)));
    Assert.Equal(CpuState.PowerLimit, c.Update(R(t1:80, perf:150, load:60)));
}
[Fact] public void PowerLimit_counter_resets_when_condition_breaks()
{
    var c = new CpuStateClassifier(); c.Update(R(perf:220));
    c.Update(R(t1:80, perf:150, load:60)); c.Update(R(t1:80, perf:150, load:60));
    c.Update(R(t1:80, perf:150, load:20));        // load drops
    Assert.NotEqual(CpuState.PowerLimit, c.Update(R(t1:80, perf:150, load:60)));
}
[Fact] public void No_PowerLimit_without_temperature_shows_turbo()   // non-elevated run
{
    var c = new CpuStateClassifier(); c.Update(R(t1:null, t0:null, perf:220));
    for (int i = 0; i < 3; i++) Assert.Equal(CpuState.Turbo, c.Update(R(t1:null, t0:null, perf:150, load:60)));
}
[Fact] public void Unknown_when_nothing_known() => Assert.Equal(CpuState.Unknown, new CpuStateClassifier().Update(R(t1:null, t0:null, perf:null)));
```
```csharp
static DiskReading D(double? t, double? warn=86) => new(new DiskInfo(0,"D:","X",null,null), t, warn, 88, 0, 0, 0);
[Fact] public void Whea_wins() => Assert.Equal(SsdState.Whea, SsdStateClassifier.Classify(new[]{D(40)}, true));
[Fact] public void Hot_at_warning_threshold() => Assert.Equal(SsdState.Hot, SsdStateClassifier.Classify(new[]{D(84, warn:84)}, false));
[Fact] public void Hot_at_80() => Assert.Equal(SsdState.Hot, SsdStateClassifier.Classify(new[]{D(80)}, false));
[Fact] public void Warm_at_70() => Assert.Equal(SsdState.Warm, SsdStateClassifier.Classify(new[]{D(55), D(70)}, false));
[Fact] public void Normal_below_70() => Assert.Equal(SsdState.Normal, SsdStateClassifier.Classify(new[]{D(69)}, false));
[Fact] public void Unknown_without_temps() => Assert.Equal(SsdState.Unknown, SsdStateClassifier.Classify(new[]{D(null)}, false));
```

- [ ] **Step 3: Run tests, expect FAIL** — `dotnet test` → compile errors (types missing).
- [ ] **Step 4: Implement models and the two classifiers** in the files above.
- [ ] **Step 5: Run tests, expect PASS** — `dotnet test` → all green.
- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat: scaffold, models and state classifiers"`

---

### Task 2: WHEA parsing, attribution, tracker

**Files:**
- Create: `src/PcMonitor/Whea/WheaParser.cs`, `WheaAttribution.cs`, `WheaTracker.cs`
- Test: `tests/PcMonitor.Tests/WheaTests.cs`

**Interfaces:**
- Consumes: `Bdf`, `DiskInfo` (Task 1).
- Produces:
  - `static class WheaParser { bool TryParseBdf(string message, out Bdf bdf); }` — first match of regex `Bus[^:]*:(?:Device|Dispositivo):(?:Function|Funzione):\s*0x([0-9A-Fa-f]+):0x([0-9A-Fa-f]+):0x([0-9A-Fa-f]+)` (the primary B:D:F).
  - `enum WheaSide { Endpoint, RootPort, Other }`; `readonly record struct WheaTarget(int? DiskNumber, WheaSide Side)`
  - `sealed class WheaAttribution { WheaAttribution(IEnumerable<DiskInfo> disks); WheaTarget Attribute(Bdf? bdf); }` — Endpoint match → (disk, Endpoint); RootPort match → (disk, RootPort); else (null, Other).
  - `sealed class WheaTracker { WheaTracker(DateTime bootTime); void Add(DateTime time, WheaTarget t); int CountToday(int? disk, WheaSide side, DateTime now); int CountSinceBoot(int? disk, WheaSide side); DateTime? LastEvent(int disk); bool SsdWheaActive(DateTime now); bool TryBeginBurst(DateTime now, out int countLast60s); WheaPeriodCounts TakePeriodCounts(); }`
  - `sealed record WheaPeriodCounts(IReadOnlyDictionary<(int Disk, WheaSide Side), int> ByDisk, int Other)`
- Rules: `SsdWheaActive` = any disk-attributed event (Endpoint or RootPort) with `now - time <= 10 min`. Burst = ≥ 10 events of any kind in the last 60 s; `TryBeginBurst` returns true at most once per 10 minutes. "Today" = local calendar date of `now`. Memory bound: keep per-minute buckets (last 24 h) plus counters, not individual events.

- [ ] **Step 1: Write failing tests**

```csharp
const string It = "Componente: PCI Express Endpoint\r\nBus primario:Dispositivo:Funzione: 0x2:0x0:0x0\r\nBus secondario:Dispositivo:Funzione: 0x0:0x0:0x0";
const string En = "Primary Bus:Device:Function: 0x0:0x6:0x2\r\nSecondary Bus:Device:Function: 0x0:0x0:0x0";
static readonly DiskInfo Crucial = new(0, "D:", "CT1000T500SSD8", new Bdf(1,0,0), new Bdf(0,6,0));
static readonly DiskInfo Wd      = new(1, "C:", "WD PC SN5000S",  new Bdf(2,0,0), new Bdf(0,6,2));

[Fact] public void Parses_italian() { Assert.True(WheaParser.TryParseBdf(It, out var b)); Assert.Equal(new Bdf(2,0,0), b); }
[Fact] public void Parses_english_root_port() { Assert.True(WheaParser.TryParseBdf(En, out var b)); Assert.Equal(new Bdf(0,6,2), b); }
[Fact] public void No_bdf_in_cpu_event() => Assert.False(WheaParser.TryParseBdf("Componente: Processore\r\nOrigine errore: Machine Check Exception", out _));
[Fact] public void Attributes_endpoint_rootport_other()
{
    var a = new WheaAttribution(new[]{ Crucial, Wd });
    Assert.Equal(new WheaTarget(1, WheaSide.Endpoint), a.Attribute(new Bdf(2,0,0)));
    Assert.Equal(new WheaTarget(0, WheaSide.RootPort), a.Attribute(new Bdf(0,6,0)));
    Assert.Equal(new WheaTarget(null, WheaSide.Other), a.Attribute(new Bdf(0,0x1f,3)));
    Assert.Equal(new WheaTarget(null, WheaSide.Other), a.Attribute(null));
}
[Fact] public void Purple_window_is_10_minutes()
{
    var t0 = new DateTime(2026,11,24,12,0,0); var tr = new WheaTracker(t0.AddHours(-1));
    tr.Add(t0, new WheaTarget(0, WheaSide.Endpoint));
    Assert.True(tr.SsdWheaActive(t0.AddMinutes(10)));
    Assert.False(tr.SsdWheaActive(t0.AddMinutes(10).AddSeconds(1)));
}
[Fact] public void Other_events_do_not_turn_purple()
{
    var t0 = new DateTime(2026,11,24,12,0,0); var tr = new WheaTracker(t0);
    tr.Add(t0, new WheaTarget(null, WheaSide.Other));
    Assert.False(tr.SsdWheaActive(t0));
}
[Fact] public void Burst_fires_once_per_10_minutes()
{
    var t0 = new DateTime(2026,11,24,12,0,0); var tr = new WheaTracker(t0);
    for (int i = 0; i < 9; i++) tr.Add(t0.AddSeconds(i), new WheaTarget(1, WheaSide.Endpoint));
    Assert.False(tr.TryBeginBurst(t0.AddSeconds(9), out _));
    tr.Add(t0.AddSeconds(9), new WheaTarget(1, WheaSide.Endpoint));
    Assert.True(tr.TryBeginBurst(t0.AddSeconds(10), out var n)); Assert.Equal(10, n);
    for (int i = 0; i < 20; i++) tr.Add(t0.AddMinutes(5).AddSeconds(i), new WheaTarget(1, WheaSide.Endpoint));
    Assert.False(tr.TryBeginBurst(t0.AddMinutes(5).AddSeconds(30), out _));
    for (int i = 0; i < 20; i++) tr.Add(t0.AddMinutes(11).AddSeconds(i), new WheaTarget(1, WheaSide.Endpoint));
    Assert.True(tr.TryBeginBurst(t0.AddMinutes(11).AddSeconds(30), out _));
}
[Fact] public void Today_resets_at_midnight_since_boot_does_not()
{
    var boot = new DateTime(2026,11,24,20,0,0); var tr = new WheaTracker(boot);
    tr.Add(new DateTime(2026,11,24,23,59,0), new WheaTarget(0, WheaSide.Endpoint));
    Assert.Equal(1, tr.CountToday(0, WheaSide.Endpoint, new DateTime(2026,11,24,23,59,30)));
    Assert.Equal(0, tr.CountToday(0, WheaSide.Endpoint, new DateTime(2026,11,25,0,0,1)));
    Assert.Equal(1, tr.CountSinceBoot(0, WheaSide.Endpoint));
}
[Fact] public void Period_counts_reset_after_take()
{
    var t0 = new DateTime(2026,11,24,12,0,0); var tr = new WheaTracker(t0);
    tr.Add(t0, new WheaTarget(1, WheaSide.RootPort)); tr.Add(t0, new WheaTarget(null, WheaSide.Other));
    var p = tr.TakePeriodCounts();
    Assert.Equal(1, p.ByDisk[(1, WheaSide.RootPort)]); Assert.Equal(1, p.Other);
    Assert.Equal(0, tr.TakePeriodCounts().Other);
}
[Fact] public void Storm_of_100k_events_stays_bounded()
{
    var t0 = new DateTime(2026,11,24,12,0,0); var tr = new WheaTracker(t0);
    long before = GC.GetTotalMemory(true);
    for (int i = 0; i < 100_000; i++) tr.Add(t0.AddMilliseconds(i * 20), new WheaTarget(0, WheaSide.Endpoint));
    Assert.True(GC.GetTotalMemory(true) - before < 2_000_000);
    Assert.Equal(100_000, tr.CountSinceBoot(0, WheaSide.Endpoint));
}
```

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter WheaTests`
- [ ] **Step 3: Implement** the three classes. `WheaTracker` is called from the WHEA thread and read from the UI/timer thread: guard with a single `lock`.
- [ ] **Step 4: Run, expect PASS** — `dotnet test --filter WheaTests`
- [ ] **Step 5: Commit** — `git commit -am "feat: WHEA parsing, attribution and tracking"` (add new files first)

---

### Task 3: Ring buffer and event journal

**Files:**
- Create: `src/PcMonitor/Logic/RingBuffer.cs`, `src/PcMonitor/Logic/EventJournal.cs`
- Test: `tests/PcMonitor.Tests/JournalTests.cs`

**Interfaces:**
- Produces:
  - `sealed class RingBuffer<T> where T : struct { RingBuffer(int capacity); void Add(T item); int Count { get; } T this[int i] { get; } }` — index 0 = oldest.
  - `struct HistorySample { float T1, PackageW, TempD, TempC; }` (NaN = missing) in `RingBuffer.cs`.
  - `sealed record JournalEntry(DateTime Time, string Text)`
  - `sealed class EventJournal { EventJournal(int capacity = 200); event Action<JournalEntry>? Added; void Add(DateTime t, string text); void ObserveCpu(DateTime now, CpuState s, double? t1); void ObserveSsd(DateTime now, SsdState s, string detail); IReadOnlyList<JournalEntry> Entries { get; } }` — `Entries` newest first.
- Journal texts (exact, Italian):
  - leaving Thermal: `CPU freno termico, {peakT1:0} C, durata {seconds} s`
  - leaving PowerLimit: `CPU limite di potenza, durata {seconds} s`
  - entering SSD Hot: `SSD caldo: {detail}`; entering SSD Whea: `Errori PCIe: {detail}`
  - peak T1 = max T1 observed during the episode; null T1 shows `n/d`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Ring_keeps_last_N_oldest_first()
{
    var r = new RingBuffer<int>(3); for (int i = 1; i <= 5; i++) r.Add(i);
    Assert.Equal(3, r.Count); Assert.Equal(3, r[0]); Assert.Equal(5, r[2]);
}
[Fact] public void Thermal_episode_logged_on_exit_with_peak_and_duration()
{
    var j = new EventJournal(); var t = new DateTime(2026,11,24,17,40,0);
    j.ObserveCpu(t, CpuState.Thermal, 95); j.ObserveCpu(t.AddSeconds(20), CpuState.Thermal, 96);
    j.ObserveCpu(t.AddSeconds(45), CpuState.Turbo, 88);
    Assert.Equal("CPU freno termico, 96 C, durata 45 s", j.Entries[0].Text);
    Assert.Equal(t.AddSeconds(45), j.Entries[0].Time);
}
[Fact] public void PowerLimit_episode_logged()
{
    var j = new EventJournal(); var t = DateTime.Today;
    j.ObserveCpu(t, CpuState.PowerLimit, 80); j.ObserveCpu(t.AddSeconds(12), CpuState.Normal, 70);
    Assert.Equal("CPU limite di potenza, durata 12 s", j.Entries[0].Text);
}
[Fact] public void Ssd_entries_only_on_entering()
{
    var j = new EventJournal(); var t = DateTime.Today;
    j.ObserveSsd(t, SsdState.Whea, "bus 2, 12 eventi"); j.ObserveSsd(t.AddSeconds(1), SsdState.Whea, "x");
    Assert.Single(j.Entries); Assert.Equal("Errori PCIe: bus 2, 12 eventi", j.Entries[0].Text);
}
[Fact] public void Capacity_200()
{
    var j = new EventJournal(); for (int i = 0; i < 250; i++) j.Add(DateTime.Today, i.ToString());
    Assert.Equal(200, j.Entries.Count); Assert.Equal("249", j.Entries[0].Text);
}
```

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter JournalTests`
- [ ] **Step 3: Implement** both classes (journal guarded by a lock; `Added` raised outside the lock).
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat: ring buffer and event journal"`

---

### Task 4: CSV logger

**Files:**
- Create: `src/PcMonitor/Logging/CsvLogger.cs`
- Test: `tests/PcMonitor.Tests/CsvLoggerTests.cs`

**Interfaces:**
- Consumes: `CpuState`, `WheaPeriodCounts`, `JournalEntry`.
- Produces:
  - `sealed record CsvRow(DateTime Time, double? T1, double? T0Max, double? PackageW, double? IaW, double? PerfPct, double? LoadPct, double? LimitPct, CpuState CpuState, double? Tzs0, double? Tzs1, double? TempD, double? TempC, double? ReadD, double? WriteD, double? ReadC, double? WriteC, int WheaDEp, int WheaDRp, int WheaCEp, int WheaCRp, int WheaOther, bool? OnAc, int? BatteryPct, double? RateW)`
  - `sealed class CsvLogger : IDisposable { CsvLogger(string directory); void WriteRow(CsvRow r); void WriteEvent(JournalEntry e); static void Cleanup(string directory, DateTime today, int keepDays = 7); const string Header; }`
- Header (exact): `ora;cpu_c;cpu_c_max;cpu_w;core_w;turbo_pct;carico_pct;limite_pct;stato_cpu;tzs0_c;tzs1_c;d_c;c_c;d_lett_mbs;d_scritt_mbs;c_lett_mbs;c_scritt_mbs;whea_d_ep;whea_d_rp;whea_c_ep;whea_c_rp;whea_altri;ac;batteria_pct;carica_w`
- Formatting: `ora` = `HH:mm:ss`; doubles `it-IT` with 1 decimal (`F1`); null → empty; `stato_cpu` = enum name; `ac` = `1`/`0`/empty. File chosen by `r.Time.Date` → `pcmonitor-yyyyMMdd.csv`; header written when the file is new. Events: `events-yyyyMMdd.log`, line `HH:mm:ss  text`. Every write flushes. Cleanup deletes `pcmonitor-*.csv` and `events-*.log` older than `keepDays` by the date in the file name.

- [ ] **Step 1: Write failing tests** (use a fresh temp dir per test)

```csharp
static CsvRow Row(DateTime t) => new(t, 84.5, 92, 28, 21, 215, 40, 100, CpuState.Turbo, 65, 50, 51, 55, 0, 0, 1.2, 3, 0, 0, 0, 0, 0, true, 66, 26);

[Fact] public void Writes_header_and_italian_row()
{
    var dir = TempDir(); using (var l = new CsvLogger(dir)) l.WriteRow(Row(new DateTime(2026,11,24,17,0,5)));
    var lines = File.ReadAllLines(Path.Combine(dir, "pcmonitor-20261124.csv"));
    Assert.Equal(CsvLogger.Header, lines[0]);
    Assert.StartsWith("17:00:05;84,5;92,0;28,0;21,0;215,0;40,0;100,0;Turbo;", lines[1]);
    Assert.EndsWith(";1;66;26,0", lines[1]);
}
[Fact] public void Null_values_are_empty_fields()
{
    var dir = TempDir(); using (var l = new CsvLogger(dir)) l.WriteRow(Row(DateTime.Today) with { T1 = null, OnAc = null });
    var f = File.ReadAllLines(Path.Combine(dir, $"pcmonitor-{DateTime.Today:yyyyMMdd}.csv"))[1].Split(';');
    Assert.Equal("", f[1]); Assert.Equal("", f[22]);
}
[Fact] public void Switches_file_after_midnight()
{
    var dir = TempDir(); using (var l = new CsvLogger(dir)) { l.WriteRow(Row(new DateTime(2026,11,24,23,59,55))); l.WriteRow(Row(new DateTime(2026,11,25,0,0,5))); }
    Assert.True(File.Exists(Path.Combine(dir, "pcmonitor-20261124.csv")));
    Assert.Equal(2, File.ReadAllLines(Path.Combine(dir, "pcmonitor-20261125.csv")).Length);
}
[Fact] public void Row_is_readable_while_logger_open()   // flush per line, crash safety
{
    var dir = TempDir(); using var l = new CsvLogger(dir); l.WriteRow(Row(new DateTime(2026,11,24,10,0,0)));
    using var fs = new FileStream(Path.Combine(dir, "pcmonitor-20261124.csv"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    Assert.Equal(2, new StreamReader(fs).ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
}
[Fact] public void Cleanup_keeps_7_days()
{
    var dir = TempDir();
    foreach (var d in new[]{ "20261116", "20261117", "20261118", "20261124" }) { File.WriteAllText(Path.Combine(dir, $"pcmonitor-{d}.csv"), ""); File.WriteAllText(Path.Combine(dir, $"events-{d}.log"), ""); }
    CsvLogger.Cleanup(dir, new DateTime(2026,11,24));
    Assert.False(File.Exists(Path.Combine(dir, "pcmonitor-20261116.csv")));
    Assert.False(File.Exists(Path.Combine(dir, "events-20261117.log")));
    Assert.True(File.Exists(Path.Combine(dir, "pcmonitor-20261118.csv")));
}
[Fact] public void Writes_event_line()
{
    var dir = TempDir(); using (var l = new CsvLogger(dir)) l.WriteEvent(new JournalEntry(new DateTime(2026,11,24,17,40,12), "CPU freno termico, 96 C, durata 45 s"));
    Assert.Equal("17:40:12  CPU freno termico, 96 C, durata 45 s", File.ReadAllLines(Path.Combine(dir, "events-20261124.log"))[0]);
}
```

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter CsvLoggerTests`
- [ ] **Step 3: Implement** `CsvLogger` (`FileShare.ReadWrite`, `AutoFlush = true`, UTF-8 with BOM so Excel opens accents correctly).
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** — `git add -A && git commit -m "feat: daily CSV and event logger"`

---

### Task 5: PDH wrapper, CPU sensor, disk throughput

**Files:**
- Create: `src/PcMonitor/Native/Pdh.cs`, `src/PcMonitor/Sensors/CpuSensor.cs`, `src/PcMonitor/Sensors/DiskSensor.cs` (only `DiskThroughput` for now; `DiskSensor` is added in Task 6)
- Modify: `src/PcMonitor/Program.cs` (add `--probe`)
- Test: `tests/PcMonitor.Tests/CpuSensorTests.cs`

**Interfaces:**
- Produces:
  - `sealed class PdhQuery : IDisposable { static PdhQuery? TryCreate(params string[] englishPaths); bool Collect(); IReadOnlyDictionary<string, double> Values(string englishPath); }` — wildcard paths return instance→value; non-wildcard return key `""`. `TryCreate` returns null if any counter cannot be added. `Collect` returns false on PDH error (no throw). `Values` returns empty on error.
  - `sealed class CpuSensor : IDisposable { CpuSensor(); bool EsifAvailable { get; } CpuReading Read(); static CpuReading Map(IReadOnlyDictionary<string,double> esifTemp, IReadOnlyDictionary<string,double> esifRapl, double? perf, double? load, double? limit, IReadOnlyDictionary<string,double> tzTemp, IReadOnlyDictionary<string,double> tzPassive); }`
  - `sealed class DiskThroughput : IDisposable { bool Collect(); (double? ReadMBs, double? WriteMBs, double? LatencyMs) Get(int diskNumber); }` in `src/PcMonitor/Sensors/DiskSensor.cs`.
- Counter paths (English):
  - ESIF query (separate, may fail non-admin): `\EsifDeviceInformation(*)\Temperature`, `\EsifDeviceInformation(*)\RAPL Power`
  - Base query: `\Processor Information(_Total)\% Processor Performance`, `\Processor Information(_Total)\% Processor Utility`, `\Processor Information(_Total)\% Performance Limit`, `\Thermal Zone Information(*)\High Precision Temperature`, `\Thermal Zone Information(*)\% Passive Limit`
  - Disk query: `\PhysicalDisk(*)\Disk Read Bytes/sec`, `\PhysicalDisk(*)\Disk Write Bytes/sec`, `\PhysicalDisk(*)\Avg. Disk sec/Transfer` (instance name starts with the disk number, e.g. `"0 D:"`).
- Map rules: T1 = ESIF Temperature of the instance whose name ends with `_1`; T0 = `_0`; PackageW = RAPL `_0` / 10; IaW = RAPL `_1` / 10; a value of 0 for T1/T0 means missing (null). Zone temp = HighPrecisionTemperature / 10 − 273.15, zone name = text after the last `.` (`\_TZ.TZS0` → `TZS0`). Missing dictionary → nulls / empty list. Latency in ms = sec/Transfer × 1000.

- [ ] **Step 1: Write failing tests** (pure `Map` + a machine test)

```csharp
const string P = @"pci\ven_8086&dev_7d03&subsys_381417aa&rev_05\3&11583659&1&20_";
[Fact] public void Maps_esif_by_suffix()
{
    var r = CpuSensor.Map(new Dictionary<string,double>{{P+"0",92},{P+"1",84},{P+"2",84},{P+"3",61}},
                          new Dictionary<string,double>{{P+"0",280},{P+"1",209}}, 215, 40, 100,
                          new Dictionary<string,double>{{@"\_TZ.TZS0",3382}}, new Dictionary<string,double>{{@"\_TZ.TZS0",100}});
    Assert.Equal(84, r.T1); Assert.Equal(92, r.T0); Assert.Equal(28.0, r.PackageW!.Value, 1); Assert.Equal(20.9, r.IaW!.Value, 1);
    Assert.Equal("TZS0", r.Zones[0].Name); Assert.Equal(65.05, r.Zones[0].TempC, 1);
}
[Fact] public void Missing_esif_gives_null_temps()   // non-elevated or standby glitch
{
    var r = CpuSensor.Map(new Dictionary<string,double>(), new Dictionary<string,double>(), 120, 10, 100, new Dictionary<string,double>(), new Dictionary<string,double>());
    Assert.Null(r.T1); Assert.Null(r.PackageW); Assert.Equal(120, r.PerfPct); Assert.Empty(r.Zones);
}
[Fact] public void Zero_temperature_is_missing()
    => Assert.Null(CpuSensor.Map(new Dictionary<string,double>{{P+"1",0}}, new Dictionary<string,double>(), null, null, null, new Dictionary<string,double>(), new Dictionary<string,double>()).T1);
[Fact] public void Base_counters_work_on_this_machine()
{
    using var s = new CpuSensor(); s.Read(); Thread.Sleep(1100); var r = s.Read();
    Assert.NotNull(r.PerfPct); Assert.NotNull(r.LoadPct);
}
```

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter CpuSensorTests`
- [ ] **Step 3: Implement** `Pdh.cs` (P/Invoke: `PdhOpenQueryW`, `PdhAddEnglishCounterW`, `PdhCollectQueryData`, `PdhGetFormattedCounterArrayW` with `PDH_FMT_DOUBLE | PDH_FMT_NOCAP100`, `PdhGetFormattedCounterValue`, `PdhCloseQuery`), `CpuSensor`, `DiskThroughput`. PDH rate counters need two collects: first `Read()` may return nulls.
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Add `--probe`**: `Program.Main` with `--probe` calls `AttachConsole(-1)`, reads every sensor available so far twice 1 s apart, prints each `CpuReading` field and disk throughput, exits 0.
- [ ] **Step 6: Verify on machine** — run elevated: `dotnet run --project src/PcMonitor -- --probe` → T1 between 30 and 105, PackageW between 1 and 60, PerfPct > 0. Run non-elevated (`runas /trustlevel:0x20000 ...` writing to a file) → T1 = null, PerfPct present, no exception.
- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: PDH wrapper, CPU sensor and disk throughput"`

---

### Task 6: Disk topology, NVMe temperature, power sensor

**Files:**
- Create: `src/PcMonitor/Native/DeviceTree.cs`, `src/PcMonitor/Native/Nvme.cs`, `src/PcMonitor/Native/Battery.cs`, `src/PcMonitor/Sensors/DiskTopology.cs`, `src/PcMonitor/Sensors/DiskSensor.cs` (add to existing), `src/PcMonitor/Sensors/PowerSensor.cs`
- Modify: `src/PcMonitor/Program.cs` (`--probe` prints disks and power)
- Test: `tests/PcMonitor.Tests/HardwareTests.cs`

**Interfaces:**
- Consumes: `DiskInfo`, `DiskReading`, `PowerReading`, `Bdf`, `DiskThroughput`.
- Produces:
  - `static class DiskTopology { IReadOnlyList<DiskInfo> Discover(); }` — fixed NVMe/SATA disks that have a drive letter; Letter = lowest letter on that disk (`"C:"`).
  - `static class NvmeTemperature { (double? TempC, double? WarnC, double? CritC) Query(int diskNumber); }`
  - `sealed class DiskSensor : IDisposable { DiskSensor(IReadOnlyList<DiskInfo> disks); IReadOnlyList<DiskReading> Read(bool refreshTemps); }` — temps cached between refreshes.
  - `static class PowerSensor { PowerReading Read(bool refreshRate); }` — rate cached between refreshes.
- Native details:
  - Letter → disk: `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` (0x560000) on `\\.\X:` (access 0).
  - Model: `IOCTL_STORAGE_QUERY_PROPERTY` PropertyId 0 (StorageDeviceProperty), ProductId string.
  - Disk number → device instance: enumerate `GUID_DEVINTERFACE_DISK` with SetupAPI, `IOCTL_STORAGE_GET_DEVICE_NUMBER` (0x2D1080) on each interface path, take its devinst. Endpoint = parent devinst (NVMe controller); RootPort = parent of that. B:D:F from `DEVPKEY_Device_BusNumber` (UINT32) and `DEVPKEY_Device_Address` (UINT32: device = high word, function = low word) via `CM_Get_DevNode_PropertyW`. Never parse the localized LocationInfo string.
  - NVMe temperature: `IOCTL_STORAGE_QUERY_PROPERTY` (0x2D1400), PropertyId **52**, QueryType 0, access mask 0. Output `STORAGE_TEMPERATURE_DATA_DESCRIPTOR`: CriticalTemperature int16 @8, WarningTemperature int16 @10, InfoCount uint16 @12, TemperatureInfo[] @24 (16 bytes each, Temperature int16 @+2). Use sensor index 0.
  - Power: `GetSystemPowerStatus` (ACLineStatus, BatteryLifePercent; 255 = unknown → null). Rate: SetupAPI on battery interface GUID `{72631E54-78A4-11D0-BCF7-00AA00B7B32A}`, `IOCTL_BATTERY_QUERY_TAG` (0x294040), `IOCTL_BATTERY_QUERY_STATUS` (0x29404C); `BATTERY_STATUS.Rate` (int32 mW, `0x80000000` = unknown) / 1000 → RateW.

- [ ] **Step 1: Write failing machine tests** (this hardware; spec values)

```csharp
[Fact] public void Discovers_both_ssds_with_slot_mapping()
{
    var d = DiskTopology.Discover();
    var crucial = d.Single(x => x.Model.Contains("CT1000T500"));
    var wd = d.Single(x => x.Model.Contains("SN5000S"));
    Assert.Equal("D:", crucial.Letter); Assert.Equal("C:", wd.Letter);
    Assert.Equal(new Bdf(1,0,0), crucial.Endpoint); Assert.Equal(new Bdf(0,6,0), crucial.RootPort);
    Assert.Equal(new Bdf(2,0,0), wd.Endpoint);      Assert.Equal(new Bdf(0,6,2), wd.RootPort);
}
[Fact] public void Reads_nvme_temperature_and_thresholds()
{
    var crucial = DiskTopology.Discover().Single(x => x.Model.Contains("CT1000T500"));
    var (t, warn, crit) = NvmeTemperature.Query(crucial.DiskNumber);
    Assert.InRange(t!.Value, 20, 90); Assert.Equal(86, warn); Assert.Equal(88, crit);
}
[Fact] public void Nonexistent_disk_returns_nulls() => Assert.Null(NvmeTemperature.Query(99).TempC);
[Fact] public void Power_reading_on_ac() { var p = PowerSensor.Read(true); Assert.NotNull(p.OnAc); Assert.InRange(p.BatteryPct!.Value, 0, 100); }
```

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter HardwareTests`
- [ ] **Step 3: Implement** native helpers, `DiskTopology`, `NvmeTemperature`, `DiskSensor`, `PowerSensor`.
- [ ] **Step 4: Run, expect PASS** (both elevated and non-elevated: these sources must not need admin).
- [ ] **Step 5: Extend `--probe`** to print each `DiskReading` and the `PowerReading`; run and compare with `Get-PhysicalDisk | Get-StorageReliabilityCounter` (temperatures within 2 °C).
- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat: disk topology, NVMe temperature and power sensor"`

---

### Task 7: WHEA live source

**Files:**
- Create: `src/PcMonitor/Whea/WheaSource.cs`
- Modify: `src/PcMonitor/PcMonitor.csproj` (add `System.Diagnostics.EventLog` package), `Program.cs` (`--probe` prints WHEA since boot per disk/side)
- Test: `tests/PcMonitor.Tests/WheaSourceTests.cs`

**Interfaces:**
- Consumes: `WheaParser`, `WheaAttribution`, `WheaTracker`.
- Produces: `sealed class WheaSource : IDisposable { WheaSource(WheaAttribution a, WheaTracker t); void ScanSinceBoot(DateTime bootTime); void Start(); event Action<DateTime, WheaTarget>? EventArrived; }`
- Query: log `System`, XPath `*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='{boot:o UTC}']]]`. Boot time from `Environment.TickCount64`. `ScanSinceBoot` runs on a background thread, reads in batches (`EventLogReader.BatchSize = 256`), uses `FormatDescription()` for the message. Live: `EventLogWatcher` with the provider-only XPath; the handler parses, calls `tracker.Add`, raises `EventArrived`, nothing else (cheap under storms). Watcher errors are swallowed and the watcher re-created on the next `Start()` call.

- [ ] **Step 1: Write failing test**

```csharp
[Fact] public void Scan_since_boot_matches_event_log_count()
{
    var boot = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);
    var tracker = new WheaTracker(boot);
    using var src = new WheaSource(new WheaAttribution(DiskTopology.Discover()), tracker);
    src.ScanSinceBoot(boot);
    var q = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]");
    int expected = 0; using (var r = new EventLogReader(q)) for (var e = r.ReadEvent(); e != null; e = r.ReadEvent()) if (e.TimeCreated >= boot) expected++;
    int total = new[]{0,1}.Sum(d => tracker.CountSinceBoot(d, WheaSide.Endpoint) + tracker.CountSinceBoot(d, WheaSide.RootPort)) + tracker.CountSinceBoot(null, WheaSide.Other);
    Assert.Equal(expected, total);
}
```

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter WheaSourceTests`
- [ ] **Step 3: Implement** `WheaSource`.
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Verify against history** — temporarily construct a tracker with boot time `2026-11-24 08:00` and scan: expect 4115 events attributed to the WD/Crucial according to *today's* topology (they occurred before the swap, so they land on disk bus 2 = WD now; this confirms parsing, not the historical slot). Remove the temporary code.
- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat: WHEA live source and boot scan"`

---

### Task 8: Monitor orchestrator and tray icons

**Files:**
- Create: `src/PcMonitor/Monitor.cs`, `src/PcMonitor/UI/Palette.cs`, `src/PcMonitor/UI/IconRenderer.cs`, `src/PcMonitor/UI/TrayController.cs`
- Modify: `src/PcMonitor/Program.cs` (normal start: `Application.Run` with `TrayController`)
- Test: `tests/PcMonitor.Tests/MonitorTests.cs`, `tests/PcMonitor.Tests/IconTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `sealed record MonitorSnapshot(DateTime Time, CpuReading Cpu, CpuState CpuState, double? T1Min, double? T1Max, IReadOnlyList<DiskReading> Disks, SsdState SsdState, PowerReading Power, bool Elevated, bool EsifAvailable)`
  - `interface ISensors { CpuReading ReadCpu(); IReadOnlyList<DiskReading> ReadDisks(bool refreshTemps); PowerReading ReadPower(bool refreshRate); }` + `sealed class RealSensors : ISensors, IDisposable`
  - `sealed class Monitor : IDisposable { Monitor(ISensors s, WheaTracker w, EventJournal j, CsvLogger? log, Func<DateTime> clock); MonitorSnapshot Tick(int tickIndex); RingBuffer<HistorySample> History { get; } WheaTracker Whea { get; } EventJournal Journal { get; } event Action<MonitorSnapshot>? Updated; void Start(); }` — `Start` runs a `System.Threading.Timer` every 1000 ms calling `Tick`; `Tick` is public for tests. Temps refresh when `tickIndex % 5 == 0`, battery rate and CSV row when `tickIndex % 10 == 0`. Each sensor call is wrapped in try/catch → previous value or null readings. Journal entries are forwarded to `CsvLogger.WriteEvent`.
  - `static class IconRenderer { Icon Render(string text, Color background, int sizePx); }` — caller owns and disposes; `TrayController` calls `DestroyIcon` on the previous handle.
  - `static class Palette` — `Normal`, `Turbo`, `PowerLimit`, `Thermal`, `Whea`, `Unknown` colours; white text.
  - `sealed class TrayController : ApplicationContext { TrayController(Monitor m, WheaSource w); }` — two `NotifyIcon`s, context menu `Apri` / `Apri cartella log` / `Esci`, left click opens `DetailForm` (Task 9; until then a placeholder `MessageBox`-free no-op).
- Icon text: CPU = T1 rounded (`!!` if ≥ 100, `--` if null); SSD = max disk temp (same rules). Colours: CpuState/SsdState → Palette (Warm → PowerLimit orange, Hot → Thermal red). Icon size = `SystemInformation.SmallIconSize.Width` (re-read on `DpiChanged`/`DisplaySettingsChanged`).
- Tooltips (exact format, truncated to 127 chars):
  - CPU: `CPU {T1:0} C - {W:0} W - turbo {perf:0}% - carico {load:0}% - {stato}` with stato ∈ `normale`, `turbo`, `limite di potenza (stima)`, `freno termico`, `n/d`.
  - SSD: `{letter} {short model} {temp:0} C - ...` per disk, then ` - WHEA oggi: {n}` (endpoint + root port, all disks).
- Burst toast: when `Whea.TryBeginBurst` is true, `NotifyIcon.ShowBalloonTip(10000, "PcMonitor - errori PCIe", "{n} errori WHEA nell'ultimo minuto ({disk} bus {bus})", ToolTipIcon.Warning)`.

- [ ] **Step 1: Write failing tests**

```csharp
sealed class FakeSensors : ISensors {
    public Func<CpuReading> Cpu = () => new(70, 75, 20, 15, 200, 30, 100, new List<ThermalZone>());
    public bool Throw;
    public CpuReading ReadCpu() => Throw ? throw new InvalidOperationException() : Cpu();
    public IReadOnlyList<DiskReading> ReadDisks(bool r) => new[]{ new DiskReading(new DiskInfo(0,"D:","CT1000T500SSD8",new Bdf(1,0,0),new Bdf(0,6,0)), 50, 86, 88, 0, 0, 0) };
    public PowerReading ReadPower(bool r) => new(true, 66, 26);
}
[Fact] public void Tick_builds_snapshot_and_history()
{
    var t = new DateTime(2026,11,24,18,0,0);
    using var m = new Monitor(new FakeSensors(), new WheaTracker(t), new EventJournal(), null, () => t);
    var s = m.Tick(0);
    Assert.Equal(CpuState.Turbo, s.CpuState); Assert.Equal(SsdState.Normal, s.SsdState); Assert.Equal(1, m.History.Count);
}
[Fact] public void Tick_survives_throwing_sensor()
{
    var f = new FakeSensors { Throw = true }; var t = DateTime.Now;
    using var m = new Monitor(f, new WheaTracker(t), new EventJournal(), null, () => t);
    var s = m.Tick(1);
    Assert.Null(s.Cpu.T1); Assert.Equal(SsdState.Normal, s.SsdState);
}
[Fact] public void Whea_turns_snapshot_purple()
{
    var t = DateTime.Now; var w = new WheaTracker(t); w.Add(t, new WheaTarget(0, WheaSide.Endpoint));
    using var m = new Monitor(new FakeSensors(), w, new EventJournal(), null, () => t);
    Assert.Equal(SsdState.Whea, m.Tick(1).SsdState);
}
[Fact] public void Icon_renders_requested_size_and_background()
{
    using var ico = IconRenderer.Render("84", Palette.Turbo, 16); using var bmp = ico.ToBitmap();
    Assert.Equal(16, bmp.Width); Assert.Equal(Palette.Turbo.ToArgb(), bmp.GetPixel(0, 15).ToArgb());
}
[Fact] public void Rendering_1000_icons_does_not_leak_gdi()
{
    int before = GetGuiResources(Process.GetCurrentProcess().Handle, 0);
    for (int i = 0; i < 1000; i++) { using var ico = IconRenderer.Render((i % 100).ToString(), Palette.Normal, 16); DestroyIcon(ico.Handle); }
    Assert.True(GetGuiResources(Process.GetCurrentProcess().Handle, 0) - before < 20);
}
[Fact] public void Tooltip_truncated_to_127() => Assert.True(TrayController.FormatSsdTooltip(ManyDisks(10), 12345).Length <= 127);
```
(`ManyDisks(n)` = test helper returning n `DiskReading`s with model `"WD PC SN5000S SDEPMSJ-1T00-1101"`; `GetGuiResources`/`DestroyIcon` P/Invoke declared in the test file; `TrayController.FormatCpuTooltip(MonitorSnapshot)` and `FormatSsdTooltip(IReadOnlyList<DiskReading>, int wheaToday)` are `public static`.)

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter "MonitorTests|IconTests"`
- [ ] **Step 3: Implement** `Monitor`, `RealSensors`, `Palette`, `IconRenderer` (GDI+ bitmap, bold font sized to fill, `Icon.FromHandle(bmp.GetHicon())` then clone so the caller owns one handle), `TrayController`. Marshal `Updated` to the UI thread via `SynchronizationContext` captured in `TrayController`. Startup: `CsvLogger.Cleanup`, topology, `WheaSource.ScanSinceBoot` on a background task, then `Start()`. Journal `Add("avvio")` at start, `Add("uscita")` on exit.
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Verify live** — `dotnet run --project src/PcMonitor` (elevated): two icons appear with numbers matching `--probe`; hover shows tooltips; `Esci` closes and removes icons. Check `%LOCALAPPDATA%\PcMonitor\logs\` has a CSV row every 10 s.
- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat: monitor loop and tray icons"`

---

### Task 9: Detail window and charts

**Files:**
- Create: `src/PcMonitor/UI/DetailForm.cs`, `src/PcMonitor/UI/ChartControl.cs`
- Modify: `src/PcMonitor/UI/TrayController.cs` (open/focus form)
- Test: `tests/PcMonitor.Tests/DetailFormTests.cs`

**Interfaces:**
- Consumes: `Monitor` (`Updated`, `History`, `Whea`, `Journal`), `MonitorSnapshot`.
- Produces: `sealed class DetailForm : Form { DetailForm(Monitor m); static string Fmt(double? v, string unit, int decimals = 0); }`; `sealed class ChartControl : Control { void SetSeries(IReadOnlyList<(string Label, Color Color, Func<HistorySample,float> Selector)> series, RingBuffer<HistorySample> data, float min, float max); }`
- Layout (spec sketch): sections CPU, SSD (one block per disk: letter, model, bus, temp + warning, read/write MB/s, latency ms, WHEA oggi / da avvio / ultimo, endpoint and root port shown as `ep`/`rp`), Alimentazione, Eventi (journal, newest first). CPU section includes min/max T1 of the session, W package and core, turbo %, carico %, stato (PowerLimit labelled `limite di potenza (stima)`), sensori piattaforma (each zone `TZS0 65 C`, passive limit), `altri WHEA` count. Charts: CPU (T1 0-110 °C + PackageW 0-60 W on the same canvas, two colours, legend), SSD (TempD, TempC 20-90 °C). Banner when `!snapshot.EsifAvailable`: `Temperatura e watt CPU non disponibili: avvia PcMonitor con privilegi elevati (PcMonitor.exe --install).`
- Behaviour: form subscribes to `Updated` only while visible; unsubscribes and is disposed on close (`TrayController` creates a new one each time; single instance at a time, second click activates it). Dark theme colours from `Palette`. Double-buffered painting.
- `Fmt`: null → `n/d`; otherwise `it-IT` number + space + unit (`Fmt(84.4,"C")` → `"84 C"`, `Fmt(1.25,"MB/s",1)` → `"1,3 MB/s"`).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Fmt_null_is_nd() => Assert.Equal("n/d", DetailForm.Fmt(null, "C"));
[Fact] public void Fmt_italian() { Assert.Equal("84 C", DetailForm.Fmt(84.4, "C")); Assert.Equal("1,3 MB/s", DetailForm.Fmt(1.25, "MB/s", 1)); }
[Fact] public void Form_unsubscribes_on_close()
{
    var t = DateTime.Now; using var m = new Monitor(new FakeSensors(), new WheaTracker(t), new EventJournal(), null, () => t);
    var f = new DetailForm(m); f.Show(); f.Close();
    Assert.True(f.IsDisposed); m.Tick(0);   // must not throw ObjectDisposedException
}
```
(`FakeSensors` moved to `tests/PcMonitor.Tests/Fakes.cs` and shared.)

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter DetailFormTests` (tests touching forms marked with an STA attribute or run via `Thread` with `ApartmentState.STA`).
- [ ] **Step 3: Implement** `ChartControl`, `DetailForm`, wire into `TrayController`.
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Verify live** — open the window: values match tooltips; charts scroll; close and reopen 20 times, then check private bytes with the window closed (`Get-Process PcMonitor | Select PrivateMemorySize64`) stays near the first measurement (< +5 MB).
- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat: detail window with charts"`

---

### Task 10: Single instance, install, publish, footprint check

**Files:**
- Create: `src/PcMonitor/Install/TaskInstaller.cs`
- Modify: `src/PcMonitor/Program.cs`, `src/PcMonitor/PcMonitor.csproj` (publish properties, app icon), `README.md` (create: install/uninstall/probe in Italian, 15 lines max)
- Test: `tests/PcMonitor.Tests/InstallerTests.cs`

**Interfaces:**
- Produces: `static class TaskInstaller { string BuildCreateArgs(string exePath, string userName); string BuildDeleteArgs(); int Install(); int Uninstall(); }`
- `Install`: requires elevation (if not elevated, relaunch itself with `runas` verb and exit); copy running exe to `%LOCALAPPDATA%\PcMonitor\PcMonitor.exe` (if the copy fails because the file is locked, show `Chiudi PcMonitor (menu Esci) e riprova.` and exit 1); run `schtasks.exe` with `BuildCreateArgs`; start the installed exe.
- `BuildCreateArgs` exact: `/Create /F /TN "PcMonitor" /TR "\"{exePath}\"" /SC ONLOGON /RU "{userName}" /RL HIGHEST /DELAY 0000:30 /IT`
- `BuildDeleteArgs` exact: `/Delete /F /TN "PcMonitor"`
- Single instance: named mutex `Local\PcMonitor.SingleInstance`; second instance sets named `EventWaitHandle` `Local\PcMonitor.Show` and exits; the first instance waits on it (registered wait) and opens the detail window.
- Publish: `dotnet publish src/PcMonitor -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Create_args_exact()
    => Assert.Equal(@"/Create /F /TN ""PcMonitor"" /TR ""\""C:\Users\Damio\AppData\Local\PcMonitor\PcMonitor.exe\"""" /SC ONLOGON /RU ""Damio"" /RL HIGHEST /DELAY 0000:30 /IT",
                    TaskInstaller.BuildCreateArgs(@"C:\Users\Damio\AppData\Local\PcMonitor\PcMonitor.exe", "Damio"));
[Fact] public void Delete_args_exact() => Assert.Equal(@"/Delete /F /TN ""PcMonitor""", TaskInstaller.BuildDeleteArgs());
```

- [ ] **Step 2: Run, expect FAIL** — `dotnet test --filter InstallerTests`
- [ ] **Step 3: Implement** `TaskInstaller`, single-instance logic, `--install` / `--uninstall` dispatch, publish properties.
- [ ] **Step 4: Run all tests, expect PASS** — `dotnet test`
- [ ] **Step 5: Publish and install** — publish command above, then `publish\PcMonitor.exe --install` (elevated). Expected: `schtasks /Query /TN PcMonitor /V /FO LIST` shows `Esegui con privilegi più elevati` / highest, trigger at logon, delay 30 s; installed exe running with both icons.
- [ ] **Step 6: Footprint check** — after 10 minutes running with window closed: `Get-Process PcMonitor | Select CPU, PrivateMemorySize64, HandleCount` sampled twice 5 minutes apart. Expected: CPU time growth < 3 s per 5 min (< 1%), private bytes ≈ 40 MB (report actual), handle count stable (±20), GDI objects stable. Second launch of the exe opens the window of the first instance and exits.
- [ ] **Step 7: Commit** — `git add -A && git commit -m "feat: single instance, scheduled-task install, publish"`
