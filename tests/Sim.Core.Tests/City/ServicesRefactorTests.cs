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

    /// <summary>Хэши на тиках 20 + k · DayTicks / 8, k = 1..16. Сняты до рефакторинга SR1 (dev 3a0f18c), пересняты в SR2: станция ЖКХ выбирается по оценке времени в пути из скима (решение владельца 10.2 А), а не по манхэттену.</summary>
    private static readonly ulong[] _s1kAgents =
    {
        0xe0be479c951f20afUL, 0x64f6b115a59858f7UL, 0x24daccaa6ccfdf59UL, 0xceb21b3f45b0da58UL, 0x36f34c3592e4afbaUL, 0x14f320094c29f4bcUL, 0x75d72f8cb3c8fabaUL, 0x020e1f71a1069308UL,
        0x46c574bf74348586UL, 0x545f582df274ba2bUL, 0xf123e92ee8d2e174UL, 0xce4b09d55396f6a8UL, 0xf9a33e08d046471bUL, 0x7b881665d6886d63UL, 0xb1c09a6a20dfe90eUL, 0xa38b75b904153e1aUL,
    };

    private static readonly ulong[] _s1kAggregate =
    {
        0x9ba82a5b76e56cd4UL, 0xbfb86c6db63cacfcUL, 0xb8a6287c13686cddUL, 0x448639c760582c29UL, 0x764277a167c517d5UL, 0x873462e287ce0195UL, 0x4db605f6f6e1be9bUL, 0xde4f9a963fe4e84dUL,
        0xc45191830194442dUL, 0x94e01bdb4033ff8dUL, 0xcd14cc06694b7f6cUL, 0xc452bd7c4330b48aUL, 0x3d6d37820773a5d5UL, 0xbdc47d4c2cec8afcUL, 0x26c2cfb254da4da7UL, 0x0b7b659472f6c1e2UL,
    };

    private static readonly ulong[] _s10kAgents =
    {
        0x758a3dfb7bf3e5b8UL, 0xc94acd9de9c8d1c0UL, 0x9cfc250a9afda69eUL, 0xc2a9f8c428df1189UL, 0x9dbb319611c73344UL, 0x7f991324fd44e06bUL, 0xee43c40da1ce1cbdUL, 0x8d13d0c0f87aee86UL,
        0x554f93a0184ff4cdUL, 0xd00a3ea79d1a4094UL, 0xf9df93d129428f00UL, 0xc01573e5d6135517UL, 0x4cc878e9ea59cb35UL, 0xb36cb3bc8b00b665UL, 0x138d963f5e0ec7c4UL, 0x6dc793d4a18b9effUL,
    };

    private static readonly ulong[] _s10kAggregate =
    {
        0x3066cf3e5df68f66UL, 0x9b47480e2fc17e58UL, 0xb696aa40a5e825b4UL, 0x604065ca19139898UL, 0x0385eb75bdebae32UL, 0xd22bb088f4055b66UL, 0x67bd1c495674102aUL, 0x64da6586ee824c53UL,
        0xcb156a56191d638aUL, 0x76db47cc5878045fUL, 0x6a330b2a57ec84e6UL, 0x256f797a84606ad4UL, 0x98c391cf41e6f2c7UL, 0x630336fc9f70618eUL, 0x8962ce9e96bf4ee4UL, 0x0daea93a3e8f0eb3UL,
    };

    /// <summary>Сценарий с депо и заранее изношенной частью зданий; <paramref name="mode"/> - секция <c>city.services</c> со встроенной записью <c>utility</c>.</summary>
    internal static Simulation Make(string scenario, TripExecution exec, int depots, bool mode = false, int threads = 1)
    {
        ScenarioDesc d = LifecycleCalibrationTests.Load(scenario, exec);
        if (mode)
        {
            EnableServices(ref d);
        }

        Simulation sim = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
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

        return sim;
    }

    /// <summary>Режим <c>city.services</c> с одной записью <c>utility</c> - той же, что ядро строит без секции (= <c>content/base/services.json</c>).</summary>
    internal static void EnableServices(ref ScenarioDesc d)
    {
        BuildingTypeDesc[] types = d.Town.BuildingTypes is { Length: > 0 } bt ? bt : CityDesc.BaseBuildingTypes();
        int depot = Array.FindIndex(types, t => t.ServiceKind == ServiceKind.Utility);
        Assert.True(depot >= 0);
        CityServicesDesc s = CityServicesDesc.Default();
        CityServicesDesc.BaseRecords(depot, types[depot].VehicleType, out s.Records, out s.Levels, out s.Calls);
        d.Town.Services = s;
    }

    internal static ulong[] Run(string scenario, TripExecution exec, int depots)
    {
        using Simulation sim = Make(scenario, exec, depots);
        int step = sim.World.City!.DayTicks / 8;
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
