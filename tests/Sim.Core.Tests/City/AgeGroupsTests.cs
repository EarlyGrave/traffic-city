using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Save;
using Sim.Core.Tests.Gen;
using Sim.Core.Tests.Save;
using Sim.Core.Util;
using Sim.Scenarios;

namespace Sim.Core.Tests.City;

/// <summary>
/// Возрастные группы жителей (age-groups.md §7, задача RA1): без <c>city.age</c> всё побитово прежнее (тест 1), доли групп на день 0 (тест 2), рабочая сила не сдвинута
/// (тест 3), правила групп (тест 4), сейв посреди суток (тест 9); загрузчик и валидатор секции.
/// </summary>
public sealed class AgeGroupsTests
{
    // Эталоны сняты на коде до RA1 (dev f004887): хэш сразу после Create и после 1/4 суток. Переснять: TC_RA1_CAPTURE=1, вывод - в TC_RA1_OUT.
    private static readonly ulong[] _s1kRes = { 0x44f0c0916f6bf8cbUL, 0x1a82c6b1f59c85b0UL };
    private static readonly ulong[] _s10kRes = { 0x1909246568d48f96UL, 0x86d3ba2c98452498UL };

    private static ScenarioDesc Load(string name, TripExecution exec = TripExecution.Aggregate, bool age = false)
    {
        ScenarioDesc d = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", name + ".json")).Desc;
        d.Town.Residents.TripExecution = exec;
        if (age)
        {
            d.Town.Age = CityAgeDesc.Default();
        }

        return d;
    }

    private static Simulation Make(string name, TripExecution exec = TripExecution.Aggregate, bool age = false) =>
        Simulation.Create(Load(name, exec, age), new SimRuntimeOptions { WorkerThreads = 1 });

    [Theory]
    [InlineData("s1k-res", TripExecution.Agents)]
    [InlineData("s10k-res", TripExecution.Aggregate)]
    public void NoMode_HashUnchanged_AllAdults(string scenario, TripExecution exec)
    {
        // тест 1: без секции хэш прежний (эталон до RA1), группа у всех 0, сводка групп пуста
        using Simulation sim = Make(scenario, exec);
        Assert.False(sim.World.City!.Cfg.Age.Enabled);
        ulong h0 = sim.ComputeStateHash();
        sim.Step(sim.World.City!.DayTicks / 4);
        ulong h1 = sim.ComputeStateHash();
        if (Environment.GetEnvironmentVariable("TC_RA1_CAPTURE") == "1")
        {
            string line = string.Create(CultureInfo.InvariantCulture, $"{scenario} {exec}: 0x{h0:x16}UL, 0x{h1:x16}UL{Environment.NewLine}");
            File.AppendAllText(Environment.GetEnvironmentVariable("TC_RA1_OUT") ?? Path.Combine(Path.GetTempPath(), "ra1-hash.txt"), line);
            return;
        }

        Assert.Equal(scenario == "s1k-res" ? _s1kRes : _s10kRes, new[] { h0, h1 });
        CityResidents r = sim.World.City!.Residents;
        for (int i = 0; i < r.HighWater; i++)
        {
            Assert.Equal(0, r.Age[i]);
        }

        Assert.True(sim.GetCityStats(out CityStats st));
        Assert.Equal((0, 0), (st.PopChild, st.PopElderly));
    }

    [Theory]
    [InlineData("s1k-res", 2.0)]
    [InlineData("s10k-res", 1.0)]
    public void Shares_Day0_MatchParams(string scenario, double tolPp)
    {
        // тест 2: доля пожилых на день 0 - в пределах ±1 п. п. (s10k) / ±2 п. п. (s1k) от 16 %. Дети: бросок даёт 18 % (тест Roll_*), но правило
        // «не один ребёнок» (§2.3) на *-res (≈ 2 жителя на дом, много домов на одного) срезает ~6.5 п. п.: факт 11.50 % (s1k), 11.59 % (s10k).
        // Калибровка - RA5 (s10k-res-ra); здесь только коридор «не больше параметра, не меньше половины».
        using Simulation sim = Make(scenario, age: true);
        Assert.True(sim.GetCityStats(out CityStats st));
        Assert.True(st.Population > 0);
        double child = 100.0 * st.PopChild / st.Population;
        double elderly = 100.0 * st.PopElderly / st.Population;
        Assert.True(child >= 9.0 && child <= 18.0 + tolPp, $"детей {child:F2} % при N = {st.Population}");
        Assert.True(Math.Abs(elderly - 16.0) <= tolPp, $"пожилых {elderly:F2} % при N = {st.Population}");
        CityResidents r = sim.World.City!.Residents;
        Assert.True(r.HighWater > 0 && r.IsAlive(0));
        Assert.True(sim.TryGetResidentInfo(0, sim.GetResidentGeneration(0), out ResidentInfo info));
        Assert.Equal((ResidentAge)r.Age[0], info.Age);
    }

    [Fact]
    public void Workforce_SameAsWithoutMode()
    {
        // тест 3: при том же seed множество Labour, машины работников и их работа на день 0 совпадают с s10k-res без режима
        using Simulation plain = Make("s10k-res");
        using Simulation aged = Make("s10k-res", age: true);
        CityResidents a = plain.World.City!.Residents;
        CityResidents b = aged.World.City!.Residents;
        Assert.Equal(a.HighWater, b.HighWater);
        int workers = 0;
        for (int i = 0; i < a.HighWater; i++)
        {
            Assert.Equal(a.IsAlive(i), b.IsAlive(i));
            bool la = (a.Flags[i] & ResidentFlags.Labour) != 0;
            Assert.Equal(la, (b.Flags[i] & ResidentFlags.Labour) != 0);
            Assert.Equal(a.Home[i], b.Home[i]);
            if (la)
            {
                workers++;
                Assert.Equal(a.Flags[i] & ResidentFlags.HasCar, b.Flags[i] & ResidentFlags.HasCar);
                Assert.Equal(a.Work[i], b.Work[i]);
                Assert.Equal(0, b.Age[i]);
            }
        }

        Assert.True(workers > 1000);
        Assert.True(plain.GetCityStats(out CityStats sa));
        Assert.True(aged.GetCityStats(out CityStats sb));
        Assert.Equal((sa.Labour, sa.Employed, sa.Seeking), (sb.Labour, sb.Employed, sb.Seeking));
    }

    [Fact]
    public void GroupRules_Hold_OverDays()
    {
        // тест 4 (часть RA1): ни ребёнка, ни пожилого с работой, в рабочей силе или в очереди ищущих; у ребёнка нет машины; дома из одних детей на день 0 нет.
        // Туры детей (только Care) и рабочие туры по группе - RA2.
        using Simulation sim = Make("s1k-res", TripExecution.Agents, age: true);
        CheckHomesNotOnlyChildren(sim.World.City!);
        for (int day = 0; day < 3; day++)
        {
            sim.Step(sim.World.City!.DayTicks);
            CheckRules(sim.World.City!);
        }

        Assert.True(sim.World.Counters.Immigrants > 0, "нет въезда - ветка Immigrate не проверена");
        Assert.True(sim.GetCityStats(out CityStats st) && st.PopChild > 0 && st.PopElderly > 0);
    }

    [Fact]
    public void Roll_EmptyHome_NeverChild_AndCarsByGroup()
    {
        // правило «не один ребёнок» и машины по группе (§2.3) на прямых бросках
        CityResidentsDesc rd = CityDesc.Default().Residents;
        CityAgeDesc ad = CityAgeDesc.Default();
        Pcg32 rng = Pcg32.Create(7, 3);
        int children = 0, elderly = 0, elderlyCars = 0, n = 200_000;
        for (int k = 0; k < n; k++)
        {
            byte f = AgeGroups.Roll(ref rng, in rd, in ad, emptyHome: true, out byte age);
            Assert.NotEqual((byte)ResidentAge.Child, age);
            if (age == (byte)ResidentAge.Elderly)
            {
                elderly++;
                elderlyCars += f & ResidentFlags.HasCar;
                Assert.Equal(0, f & ResidentFlags.Labour);
            }

            f = AgeGroups.Roll(ref rng, in rd, in ad, emptyHome: false, out age);
            if (age == (byte)ResidentAge.Child)
            {
                children++;
                Assert.Equal(0, (int)f);
            }
        }

        Assert.InRange(100.0 * children / n, 17.0, 19.0);
        Assert.InRange(100.0 * elderly / n, 15.0, 17.0);
        Assert.InRange(100.0 * elderlyCars / elderly, 33.0, 37.0);   // car_ownership 0.7 · elderly_car_pct 50 %
    }

    [Theory]
    [InlineData("s1k-res")]
    [InlineData("s1k-res-sr")]
    public void Save_MidDay_ContinuesSameHash(string scenario)
    {
        // тест 9: круг сейва посреди суток - хэш равен, прогон дальше совпадает; группы и параметры секции переживают сейв
        using Simulation sim = Make(scenario, TripExecution.Agents, age: true);
        sim.Step((sim.World.City!.DayTicks / 2) + 137);
        byte[] raw = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = WorldSaveFixtures.Load(raw);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
        Assert.Equal(sim.World.City!.Cfg.Age, loaded.World.City!.Cfg.Age);
        Assert.Equal(sim.World.City!.Residents.Age, loaded.World.City!.Residents.Age);
        sim.Step(4_000);
        loaded.Step(4_000);
        Assert.Equal(sim.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void Validator_RejectsBadAge()
    {
        // §5: группа > 2, ребёнок/пожилой в рабочей силе, группа без режима - сейв не грузится
        using Simulation sim = Make("s1k-res", age: true);
        CityResidents r = sim.World.City!.Residents;
        int worker = -1;
        for (int i = 0; i < r.HighWater && worker < 0; i++)
        {
            worker = r.IsAlive(i) && (r.Flags[i] & ResidentFlags.Labour) != 0 ? i : -1;
        }

        Assert.True(worker >= 0);
        Assert.Equal(LoadStatus.InvalidValue, LoadTampered(sim, () => r.Age[worker] = 3));
        Assert.Equal(LoadStatus.InvalidValue, LoadTampered(sim, () => r.Age[worker] = (byte)ResidentAge.Elderly));
        Assert.Equal(LoadStatus.InvalidValue, LoadTampered(sim, () => r.Age[worker] = (byte)ResidentAge.Child));

        using Simulation plain = Make("s1k-res");
        CityResidents p = plain.World.City!.Residents;
        int idle = -1;
        for (int i = 0; i < p.HighWater && idle < 0; i++)
        {
            idle = p.IsAlive(i) && p.Flags[i] == 0 ? i : -1;
        }

        Assert.True(idle >= 0);
        Assert.Equal(LoadStatus.InvalidValue, LoadTampered(plain, () => p.Age[idle] = (byte)ResidentAge.Elderly));
    }

    [Fact]
    public void Loader_ParsesSection_AndChecksShares()
    {
        LoadedScenario sc = Parse("s1k-res", root => root["city"]!["age"] = new JsonObject
        {
            ["child_pct"] = 20,
            ["elderly_pct"] = 10,
            ["elderly_car_pct"] = 40,
            ["elderly_asc_freq_s"] = 60,
            ["elderly_slot"] = 96,
            ["elderly_step_slots"] = 36,
            ["pension_day"] = 25,
        });
        CityAgeDesc a = sc.Desc.Town.Age;
        Assert.True(a.Enabled);
        Assert.Equal((CityDesc.Q16(0.2), CityDesc.Q16(0.1), 40, 600, 96, 36, 25), (a.ChildQ16, a.ElderlyQ16, a.ElderlyCarPct, a.ElderlyAscFreqDs, a.ElderlySlot, a.ElderlyStepSlots, a.PensionDay));
        Assert.Equal(CreateStatus.Ok, CityBuild.Validate(sc.Desc));

        // пустая секция - стартовые значения §6
        Assert.Equal(CityAgeDesc.Default(), Parse("s1k-res", root => root["city"]!["age"] = new JsonObject()).Desc.Town.Age);

        // доли: C + E <= 1 - L (labour_share 0.6 -> 40 %); ровно 40 % проходит, не выходя за 1 в Q16
        CityAgeDesc edge = Parse("s1k-res", root => root["city"]!["age"] = new JsonObject { ["child_pct"] = 30, ["elderly_pct"] = 10 }).Desc.Town.Age;
        Assert.True(edge.ChildQ16 + edge.ElderlyQ16 + CityDesc.Q16(0.6) <= 65_536);
        Assert.Throws<ScenarioException>(() => Parse("s1k-res", root => root["city"]!["age"] = new JsonObject { ["child_pct"] = 30, ["elderly_pct"] = 11 }));
        Assert.Throws<ScenarioException>(() => Parse("s1k-res", root => root["city"]!["age"] = new JsonObject { ["kids_pct"] = 10 }));
        Assert.Throws<ScenarioException>(() => Parse("s1k", root => root["city"]!["age"] = new JsonObject()));

        // ядро: режим без residents и доли сверх неработающих - InvalidCity
        ScenarioDesc d = Load("s1k-res", age: true);
        d.Town.Age.ChildQ16 = 65_536 - d.Town.Residents.LabourShareQ16 - d.Town.Age.ElderlyQ16 + 1;
        Assert.Equal(CreateStatus.InvalidCity, CityBuild.Validate(in d));
        ScenarioDesc stray = Load("s1k-res");
        stray.Town.Age.PensionDay = 30;   // без режима поля секции - нули
        Assert.Equal(CreateStatus.InvalidCity, CityBuild.Validate(in stray));
    }

    private static LoadStatus LoadTampered(Simulation sim, Action tamper)
    {
        CityResidents r = sim.World.City!.Residents;
        byte[] ages = (byte[])r.Age.Clone();
        tamper();
        byte[] raw = WorldSaveFixtures.SaveBytes(sim);
        Array.Copy(ages, r.Age, ages.Length);
        var o = new LoadOptions { StrictHash = 0 };
        LoadStatus st = Simulation.Load(raw, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? loaded, out _);
        loaded?.Dispose();
        return st;
    }

    private static void CheckRules(CityWorld c)
    {
        CityResidents r = c.Residents;
        for (int i = 0; i < r.HighWater; i++)
        {
            if (!r.IsAlive(i) || r.Age[i] == (byte)ResidentAge.Adult)
            {
                continue;
            }

            Assert.True(r.Age[i] <= (byte)ResidentAge.Elderly);
            Assert.Equal(-1, r.Work[i]);
            Assert.Equal(0, r.Flags[i] & (ResidentFlags.Labour | ResidentFlags.InSeekQueue));
            if (r.Age[i] == (byte)ResidentAge.Child)
            {
                Assert.Equal(0, r.Flags[i] & ResidentFlags.HasCar);
            }

            for (int k = 0; k < CityResidents.MaxTours; k++)
            {
                Assert.NotEqual(TourKind.Work, r.PlanKind[(i * CityResidents.MaxTours) + k]);
            }
        }

        for (int k = 0; k < r.SeekQueue.Count; k++)
        {
            SeekEntry e = r.SeekQueue.At(k);
            Assert.False(r.Pool.IsAlive(e.Index, e.Generation) && r.Age[e.Index] != (byte)ResidentAge.Adult);
        }
    }

    private static void CheckHomesNotOnlyChildren(CityWorld c)
    {
        BuildingStore b = c.Buildings;
        CityResidents r = c.Residents;
        int homes = 0;
        for (int bi = 0; bi < b.HighWater; bi++)
        {
            if (!b.IsAlive(bi) || b.HeadRes[bi] < 0)
            {
                continue;
            }

            bool adult = false;
            for (int ri = b.HeadRes[bi]; ri >= 0; ri = r.NextHome[ri])
            {
                adult |= r.Age[ri] != (byte)ResidentAge.Child;
            }

            Assert.True(adult, $"дом {bi}: одни дети");
            homes++;
        }

        Assert.True(homes > 0);
    }

    private static LoadedScenario Parse(string name, Action<JsonNode> edit)
    {
        string dir = Path.Combine(GenFixture.RepoRoot(), "scenarios");
        JsonNode root = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, name + ".json")))!;
        edit(root);
        return ScenarioLoader.Parse(root.ToJsonString(), p => File.ReadAllText(Path.Combine(dir, p)), name, null, null, p => File.ReadAllBytes(Path.Combine(dir, p)));
    }
}
