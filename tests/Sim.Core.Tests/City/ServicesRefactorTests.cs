using System;
using System.Globalization;
using System.IO;
using System.Text;
using Sim.Core.Api;
using Sim.Core.City;

namespace Sim.Core.Tests.City;

/// <summary>
/// SR1 без изменения поведения (services.md §8 тест 1): <c>s1k-res-bl</c>, <c>s10k-res-bl</c> с депо ЖКХ - хэш состояния на контрольных тиках за 2 игровых суток равен эталону,
/// снятому на <c>dev</c> до рефакторинга (бригады BL -&gt; станции, очередь -&gt; кольцо записи <c>utility</c>). Переснять эталон: <c>TC_SR1_CAPTURE=1</c>, вывод - в <c>TC_SR1_OUT</c>.
/// </summary>
public sealed class ServicesRefactorTests
{
    private const int Points = 16;   // контрольных точек за 2 суток

    /// <summary>Хэши до рефакторинга (dev 3a0f18c): тики 20 + k · DayTicks / 8, k = 1..16.</summary>
    private static readonly ulong[] _s1kAgents =
    {
        0xe0be479c951f20afUL, 0x64f6b115a59858f7UL, 0x24daccaa6ccfdf59UL, 0xceb21b3f45b0da58UL, 0x36f34c3592e4afbaUL, 0x14f320094c29f4bcUL, 0x75d72f8cb3c8fabaUL, 0x020e1f71a1069308UL,
        0x92aa26eaa3632132UL, 0x3e3e1c81c2c1a38cUL, 0xecd114af4c5eb80aUL, 0xf11a38bd4047d871UL, 0xcc9aefdc084df6feUL, 0x1bea9b956a3ebb22UL, 0x3ae4663d0e8d588eUL, 0x0a77a24e96b37caaUL,
    };

    private static readonly ulong[] _s1kAggregate =
    {
        0x9ba82a5b76e56cd4UL, 0xbfb86c6db63cacfcUL, 0xb8a6287c13686cddUL, 0x448639c760582c29UL, 0x764277a167c517d5UL, 0x873462e287ce0195UL, 0x4db605f6f6e1be9bUL, 0x3a38db9dfa17c865UL,
        0x1b620f046095faf5UL, 0x39a2dc74c8c94bd0UL, 0x8d7fbdbac55c13abUL, 0x9bfaf0b1145b9ffeUL, 0x8cff8eda94e45c37UL, 0xa505ac7476f059eaUL, 0xe58f94e9444bb5cdUL, 0xc5f0c6604533fa31UL,
    };

    private static readonly ulong[] _s10kAgents =
    {
        0x758a3dfb7bf3e5b8UL, 0xc94acd9de9c8d1c0UL, 0x9cfc250a9afda69eUL, 0xc2a9f8c428df1189UL, 0x9dbb319611c73344UL, 0x7f991324fd44e06bUL, 0xee43c40da1ce1cbdUL, 0x8d13d0c0f87aee86UL,
        0x987779bbd9f04dfcUL, 0x952faf101d1c722eUL, 0xc158730802fbcbd7UL, 0xed2dacfa26095d23UL, 0xf824e91ee0e2de6cUL, 0x618043b9ddaf5acbUL, 0x2cb6dc9ad9d65ae5UL, 0xed9fdab5baa21f0dUL,
    };

    private static readonly ulong[] _s10kAggregate =
    {
        0x3066cf3e5df68f66UL, 0x9b47480e2fc17e58UL, 0xb696aa40a5e825b4UL, 0x604065ca19139898UL, 0x0385eb75bdebae32UL, 0xd22bb088f4055b66UL, 0x67bd1c495674102aUL, 0xe3c5b194664ad0e3UL,
        0x24f6ef3c855ac412UL, 0x379bbedf0b009658UL, 0xafd7c2b29245f948UL, 0x628a8721dd2d4f2fUL, 0x4a91245f2bd086c7UL, 0x912a11e9d167bb03UL, 0x057b2a9849472378UL, 0x10fbb716febd9024UL,
    };

    internal static ulong[] Run(string scenario, TripExecution exec, int depots)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(scenario, exec);
        using Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });
        sim.Step(20);
        Assert.Equal(depots, LifecycleCalibrationTests.PlaceDepots(sim, depots));
        CityWorld c = sim.World.City!;
        BuildingStore b = c.Buildings;
        for (int i = 0; i < b.HighWater; i += 7)
        {
            // износ части зданий заранее: заявки ЖКХ и выезды бригад в первые же сутки
            if (b.IsAlive(i) && b.State[i] == (byte)BuildingState.Active && c.Types.Rows[b.Type[i]].Kind != LotZone.Service)
            {
                b.Cond[i] = 7_500;
            }
        }

        int step = c.DayTicks / 8;
        var h = new ulong[Points];
        for (int k = 0; k < Points; k++)
        {
            sim.Step(step);
            h[k] = sim.ComputeStateHash();
        }

        Assert.True(sim.World.Counters.RepairsDone > 0, "ремонтов нет - тест не проверяет бригады");
        return h;
    }

    [Theory]
    [InlineData("s1k-res-bl", TripExecution.Agents, 3)]
    [InlineData("s1k-res-bl", TripExecution.Aggregate, 3)]
    [InlineData("s10k-res-bl", TripExecution.Agents, 12)]
    [InlineData("s10k-res-bl", TripExecution.Aggregate, 12)]
    public void Hash_SameAsBeforeRefactor(string scenario, TripExecution exec, int depots)
    {
        if (scenario == "s10k-res-bl" && exec == TripExecution.Agents && Environment.GetEnvironmentVariable("TC_LONG") != "1")
        {
            return;   // ≈ 6 мин в Debug: только с TC_LONG=1
        }

        ulong[] got = Run(scenario, exec, depots);
        if (Environment.GetEnvironmentVariable("TC_SR1_CAPTURE") == "1")
        {
            var sb = new StringBuilder();
            sb.Append(scenario).Append(' ').Append(exec).Append(':');
            foreach (ulong v in got)
            {
                sb.Append(" 0x").Append(v.ToString("x16", CultureInfo.InvariantCulture)).Append("UL,");
            }

            string path = Environment.GetEnvironmentVariable("TC_SR1_OUT") ?? Path.Combine(Path.GetTempPath(), "sr1-hash.txt");
            File.AppendAllText(path, sb.AppendLine().ToString());
            return;
        }

        bool agents = exec == TripExecution.Agents;
        ulong[] want = scenario == "s10k-res-bl" ? (agents ? _s10kAgents : _s10kAggregate) : agents ? _s1kAgents : _s1kAggregate;
        Assert.Equal(want, got);
    }
}
