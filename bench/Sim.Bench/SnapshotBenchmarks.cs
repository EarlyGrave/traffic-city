using BenchmarkDotNet.Attributes;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Bench;

/// <summary>Фаза 10 (снапшот) на прогретом мире s1k и s10k, 2 400 тиков прогрева (ADR-0010).</summary>
[MemoryDiagnoser]
public class SnapshotBenchmarks
{
    private Simulation _sim = null!;

    [Params("s1k", "s10k")]
    public string Scenario { get; set; } = "s1k";

    [GlobalSetup]
    public void Setup()
    {
        ScenarioDesc d = ScenarioLoader.Load(ScenarioLocator.Resolve(Scenario, null));
        _sim = Simulation.Create(in d);
        _sim.Step(2400);
    }

    [GlobalCleanup]
    public void Cleanup() => _sim.Dispose();

    [Benchmark]
    public void SnapshotPhase_Run() => SnapshotPhase.Run(_sim.World);
}
