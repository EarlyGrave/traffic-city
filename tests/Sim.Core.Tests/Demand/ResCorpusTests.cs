using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.Save;
using Sim.Headless;
using Sim.Host;
using Xunit.Abstractions;

namespace Sim.Core.Tests.Demand;

/// <summary>
/// Совместимость жителей (zoning-demand.md 10.4, урок E4): корпус <c>s1k-res-d3</c> (s1k-res на границе 3-х суток, настоящий календарь) грузится строго,
/// затем идёт дальше: CZ2 (деньги), CZ3 (люди), CZ4 (<c>res_stuck = 0</c>), DM1 (туры на жителя в коридоре). В CI - 1 сутки (Debug: полные 3 суток - минуты),
/// 3 суток - <c>TC_LONG=1</c>. Файл корпуса копируется в вывод тестов (<c>SaveCorpus/</c>), версия мира - текущая; миграцию старых версий проверяет
/// <c>SaveCorpusTests</c> (побитовое продолжение).
/// </summary>
public sealed class ResCorpusTests
{
    private const int DayTicks = 72_000;
    private const int CorpusTicks = 3 * DayTicks;

    private readonly ITestOutputHelper _out;

    public ResCorpusTests(ITestOutputHelper output) => _out = output;

    private static string CorpusFile() =>
        Path.Combine(AppContext.BaseDirectory, "SaveCorpus", $"w{Simulation.CurrentWorldVersion}-s1k-res-d3-t{CorpusTicks}.tcsave");

    [Fact]
    public void Corpus_S1kResD3_LoadsAndKeepsInvariants()
    {
        string path = CorpusFile();
        Assert.True(File.Exists(path), $"нет файла корпуса {path}: TC_REGEN_CORPUS=1 dotnet test --filter FullyQualifiedName~SaveCorpusTests");
        byte[] raw = SaveEnvelope.ReadContainer(path);
        using Simulation sim = WorldSaveFixtures.Load(raw);
        CityWorld city = sim.World.City!;
        Assert.Equal(CorpusTicks, sim.Tick);
        Assert.Equal(3, city.Day);
        Assert.Equal(0, city.Ledger.SumAll());

        bool full = Environment.GetEnvironmentVariable("TC_LONG") == "1";
        int days = full ? 3 : 1;
        long imm0 = sim.Counters.Immigrants;
        long emi0 = sim.Counters.Emigrants;
        int alive0 = city.Residents.AliveCount;
        double ratioSum = 0;
        for (int k = 1; k <= days; k++)
        {
            sim.Step(DayTicks);
            Assert.Equal(0, city.Ledger.SumAll());                      // CZ2
            Assert.Equal(0, sim.Counters.ResStuck);                     // CZ4
            sim.GetCityStats(out CityStats st);
            Assert.Equal(3 + k, city.Day);
            double workers = (double)st.Employed / st.Population;
            double expected = (workers * 2.0) + ((1 - workers) * 1.0);  // грубая оценка для DM1-коридора ниже: 1 рабочий тур + 0..2 дискреционных
            double perRes = (double)st.ToursYesterday / st.Population;
            _out.WriteLine($"day {st.Day}: pop {st.Population}, tours/res {perRes:F3}, legs car/bus/walk {st.LegsYesterdayCar}/{st.LegsYesterdayBus}/{st.LegsYesterdayWalk}");
            ratioSum += perRes / expected;
            Assert.InRange(perRes, 0.5, 2.5);                           // DM1 в абсолютном коридоре: на s1k-res замер 1.43 (DemandAcceptanceTests)
        }

        // CZ3: все живые жители в известном состоянии; Δ Alive = въехали − уехали
        int[] byState = new int[5];
        CityResidents r = city.Residents;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (r.IsAlive(i))
            {
                Assert.True(r.State[i] <= (byte)ResidentState.InLeg);
                byState[r.State[i]]++;
            }
        }

        Assert.Equal(r.AliveCount, byState[0] + byState[1] + byState[2] + byState[3] + byState[4]);
        Assert.Equal(sim.Counters.Immigrants - imm0 - (sim.Counters.Emigrants - emi0), r.AliveCount - alive0);
        _out.WriteLine($"mean tours/res over expected-estimate {ratioSum / days:F3}");
    }
}
