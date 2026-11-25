namespace PcMonitor.Tests;

/// <summary>Tests that count process-wide GDI objects or create windows must not run in parallel with each other.</summary>
[CollectionDefinition("Gdi", DisableParallelization = true)]
public sealed class GdiCollection { }
