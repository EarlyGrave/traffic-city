using BenchmarkDotNet.Attributes;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Bench;

/// <summary>Фаза 10 (снапшот) на прогретом s1k-мире, 2 400 тиков прогрева (ADR-0010).</summary>
[MemoryDiagnoser]
public class SnapshotBenchmarks
{
    private Simulation _sim = null!;

    [GlobalSetup]
    public void Setup()
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve("s1k", null));
        _sim = Simulation.Create(in d);
        _sim.Step(2400);
    }

    [GlobalCleanup]
    public void Cleanup() => _sim.Dispose();

    [Benchmark]
    public void SnapshotPhase_Run() => SnapshotPhase.Run(_sim.World);
}
