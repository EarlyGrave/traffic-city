using BenchmarkDotNet.Attributes;
using Sim.Core;

namespace Sim.Bench;

/// <summary>Заглушка: подтверждает, что BenchmarkSwitcher находит бенчмарки. Заменяется реальными.</summary>
public class PlaceholderBenchmark
{
    [Benchmark]
    public int ApiVersion() => CoreInfo.ApiVersion;
}
