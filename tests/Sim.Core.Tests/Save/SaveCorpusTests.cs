using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Tests.Gen;
using Sim.Headless;
using Sim.Host;
using Sim.Scenarios;

namespace Sim.Core.Tests.Save;

/// <summary>
/// Golden-корпус сейвов <c>tests/SaveCorpus/</c> (save-format.md §7, T6): каждый файл грузится строго, продолжение на <c>continue_ticks</c> даёт записанный хэш,
/// повторный сейв побайтно совпадает. С этапа 3.1 файлы старых версий (<c>w1-*</c>) остаются навсегда: они проходят миграцию и продолжаются тем же хэшем
/// (миграция 1 -> 2 не меняет поведение без пешеходов). <c>TC_REGEN_CORPUS=1 dotnet test tests/Sim.Core.Tests --filter FullyQualifiedName~SaveCorpusTests</c>
/// пересоздаёт только файлы текущей версии (<c>w2-*</c>): после волны 3.1 это делает последняя слитая задача (pedestrians.md §11, §16).
/// </summary>
public sealed class SaveCorpusTests
{
    private const int ContinueTicks = 200;
    // w14 (Y3): PARM 259-277, блок OUTS только при outside - пересоздаются те же Current. w13 (Y4): PARM 256-258, поведение прежнее - пересоздаются те же Current (small, s1k, terrain-demo), остальные покрыты файлами w11. w12 (TR1): бюджет не растёт - пересоздаётся только подмножество Current (small, s1k, terrain-demo), остальные случаи покрыты файлами w11 (мигрируют без смены поведения).
    private const int SubsetSinceVersion = 12;
    private const long BudgetBytes = 32L * 1024 * 1024;   // 5 МБ на w1-w3; растёт на ~1,4 МБ с каждой версией (файлы старых версий не удаляются), решено в T4: 8 МБ; w6 (ADR-0028): 10 МБ; w7 (ADR-0029): 12 МБ (корпус 11.4 МБ); w8 (ADR-0030): 15 МБ; w9 (ADR-0025): 21 МБ; w10 (ADR-0026, K1): 25 МБ; w11 (ADR-0034, PG1): 32 МБ - полный набор w11 (+5,7 МБ) и файл прогрессии
    private const long PedsFileBudgetBytes = 1024L * 1024;
    private const string RegenEnv = "TC_REGEN_CORPUS";

    private static readonly CorpusCase[] _cases =
    {
        new("small", 600, Small) { Current = true },
        new("s1k", 2400, S1k) { Current = true },
        new("terrain-demo", 600, TerrainDemo) { Current = true },   // w12 (ADR-0036, TR1): малый город на рельефе - блок TERR, PARM 241-255, раздел хэша 14
        new("s1k-peds", 2400, S1kPeds),
        new("s1k-res", 24000, S1kRes),
        new("s1k-res-d3", 216000, S1kRes),   // Z8 (E4): s1k-res на границе 3-х суток, настоящий календарь; тест Demand/ResCorpusTests
        new("small-drain", 600, Small, EvolveDrain),   // 4.3 (T4): сохранение посреди дренажа (операция сноса, закрытые полосы), w5
        new("small-edited", 600, SmallEdited, EvolveEdited),   // 4.3 (T2): сеть с дырами (ToolsTouched = 1), общие случаи w4 без дыр - остальные файлы
        new("cross-prof", 600, CrossProf),   // w8 (ADR-0030): режим профилей - каталог RPRF, Profile/Mirrored, Width, начало сетки
        new("s1k-prof", 2400, S1kProf),
        new("s1k-res-econ-d3", 216500, S1kResEcon, EvolveBudget),   // w9 (ADR-0025): сценарий s1k-res-econ, ожидающая политика в сейве
        new("s1k-res-prof", 4800, S1kResProf),   // w10, RP5 (ADR-0030): s1k-res по профилям фикстуры - участки на мировой сетке 8 м (CellX/CellZ/CellsF/CellsD/Orient в LOTS)
        new("s1k-res-park", 24000, S1kResPark),
        new("s1k-res-econ-prog", 3600, S1kResEconProg),   // w11 (ADR-0034, PG1-PG2): s1k-res-econ + секция progression - блоки PROG и CKPI, кольцо показателей посреди второго часа   // w10 (ADR-0026, K1): сценарий s1k-res-park + city.landuse (парки, пруды, вода): блоки PARK и LUSE, домашние места, обочины и стоянки зданий
    };

    private static readonly Lazy<string> _dir = new(PrepareCorpusDir);

    private static string CorpusDir => _dir.Value;

    private static bool Regenerating => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RegenEnv));

    [Fact]
    public void Corpus_HasCurrentVersion()
    {
        int cur = Simulation.CurrentWorldVersion;
        foreach (CorpusCase c in _cases)
        {
            int v = c.Current ? cur : SubsetSinceVersion - 1;
            string stem = Path.Combine(CorpusDir, c.FileStem(v));
            Assert.True(File.Exists(stem + ".tcsave") && File.Exists(stem + ".json"), Regen($"нет файлов корпуса {c.FileStem(v)} для world_version {v}"));
        }
    }

    [Fact]
    public void Corpus_FitsBudget_AndIsCopiedToTestOutput()
    {
        FileInfo[] files = new DirectoryInfo(CorpusDir).GetFiles("*.tcsave");
        Assert.NotEmpty(files);
        Assert.True(files.Sum(f => f.Length) <= BudgetBytes, $"корпус {files.Sum(f => f.Length)} Б больше бюджета {BudgetBytes} Б (save-format.md §7)");
        foreach (FileInfo f in files.Where(f => f.Name.Contains("-peds-", StringComparison.Ordinal)))
        {
            Assert.True(f.Length <= PedsFileBudgetBytes, $"{f.Name}: {f.Length} Б больше бюджета {PedsFileBudgetBytes} Б (pedestrians.md §11)");
        }
        if (!Regenerating)
        {
            // Читаем из вывода тестов (csproj копирует tests/SaveCorpus/*): без копии тест в CI искал бы файлы не там.
            Assert.Equal(Path.Combine(AppContext.BaseDirectory, "SaveCorpus"), CorpusDir);
        }
    }

    [Fact]
    public void Corpus_EveryFile_LoadsContinuesAndResavesByteEqual()
    {
        string[] files = Directory.GetFiles(CorpusDir, "*.tcsave");
        Assert.NotEmpty(files);
        foreach (string path in files)
        {
            CheckFile(path);
        }
    }

    private static void CheckFile(string path)
    {
        string name = Path.GetFileName(path);
        byte[] raw = SaveEnvelope.ReadContainer(path);
        using JsonDocument meta = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path, ".json")));
        JsonElement j = meta.RootElement;
        int fileVersion = j.GetProperty("world_version").GetInt32();
        ulong stateHash = Hex(j, "state_hash");
        int cont = j.GetProperty("continue_ticks").GetInt32();
        bool current = fileVersion == Simulation.CurrentWorldVersion;

        Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
        Assert.True(head.WorldVersion == fileVersion && head.StateHash == stateHash && head.Tick == j.GetProperty("tick").GetInt64(), Regen($"{name}: .json не совпадает с заголовком сейва"));

        var opt = new LoadOptions { StrictHash = current ? (byte)1 : (byte)0 };
        LoadStatus st = Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep);
        Assert.True(
            st == LoadStatus.Ok,
            Regen($"{name}: загрузка вернула {st} (блок 0x{rep.FailedBlock:x8}, столбец {rep.FailedColumn}, строка {rep.FailedRow})"));
        using (sim!)
        {
            Assert.Equal(fileVersion, rep.FileWorldVersion);
            if (Regenerating && !current)
            {
                // Файл старой версии не пересоздаётся (его пишет только старый код): при осознанной смене хэша (PG5: WarnLevel в разделе казны) обновляются ожидания .json.
                ulong load = sim!.ComputeStateHash();
                sim.Step(cont);
                string text = File.ReadAllText(Path.ChangeExtension(path, ".json"))
                    .Replace("\"hash_after_load\": \"" + j.GetProperty("hash_after_load").GetString() + "\"", $"\"hash_after_load\": \"{load:x16}\"", StringComparison.Ordinal)
                    .Replace("\"hash_after_continue\": \"" + j.GetProperty("hash_after_continue").GetString() + "\"", $"\"hash_after_continue\": \"{sim.ComputeStateHash():x16}\"", StringComparison.Ordinal);
                File.WriteAllText(Path.ChangeExtension(path, ".json"), text);
                return;
            }

            Assert.True(sim!.ComputeStateHash() == Hex(j, "hash_after_load"), Regen($"{name}: хэш после загрузки не равен записанному"));

            // Повторный сейв: для текущей версии побайтно равен файлу, для старых - после миграции стабилен (save -> load -> save).
            byte[] again = WorldSaveFixtures.SaveBytes(sim);
            if (current)
            {
                Assert.True(raw.AsSpan().SequenceEqual(again), Regen($"{name}: повторный сейв не равен файлу побайтно"));
            }
            else
            {
                using Simulation second = WorldSaveFixtures.Load(again);
                Assert.True(again.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(second)), $"{name}: сейв после миграции нестабилен");
            }

            sim.Step(cont);
            ulong after = sim.ComputeStateHash();

            // И текущая версия, и мигрированная (w1 -> w2: пешеходов нет, поведение прежнее) продолжаются записанным хэшем.
            Assert.True(after == Hex(j, "hash_after_continue"), Regen($"{name}: хэш через {cont} тиков {after:x16} не равен записанному {Hex(j, "hash_after_continue"):x16} (поведение симуляции изменилось)"));
        }
    }

    private static string Regen(string what) =>
        $"{what}. Схема или поведение ядра изменились без обновления корпуса - перегенерируй: " +
        $"{RegenEnv}=1 dotnet test tests/Sim.Core.Tests --filter FullyQualifiedName~SaveCorpusTests, затем закоммить tests/SaveCorpus/ отдельным коммитом " +
        "(до этапа 3 корпус перегенерируется, а не мигрируется).";

    private static ulong Hex(JsonElement j, string name) =>
        ulong.Parse(j.GetProperty(name).GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static string PrepareCorpusDir()
    {
        if (!Regenerating)
        {
            return Path.Combine(AppContext.BaseDirectory, "SaveCorpus");
        }

        string dir = Path.Combine(GenFixture.RepoRoot(), "tests", "SaveCorpus");
        Directory.CreateDirectory(dir);
        string prefix = $"w{Simulation.CurrentWorldVersion}-";
        foreach (string old in Directory.GetFiles(dir, prefix + "*").Where(f => f.EndsWith(".tcsave", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal)))
        {
            File.Delete(old);
        }

        foreach (CorpusCase c in _cases)
        {
            if (c.Current)
            {
                Generate(c, dir);
            }
        }

        return dir;
    }

    private static void Generate(CorpusCase c, string dir)
    {
        (ScenarioDesc desc, string[] typeNames) = c.Build();
        using Simulation sim = Simulation.Create(desc, new SimRuntimeOptions { WorkerThreads = 1 });
        AssetManifestBuilder.ForVehicleTypes(typeNames, desc.VehicleTypes, out AssetRef[] refs, out byte[] ids);
        Assert.True(sim.SetAssetManifest(refs, ids));
        if (c.Evolve != null)
        {
            c.Evolve(sim, c.Ticks);
        }
        else
        {
            sim.Step(c.Ticks);
        }

        byte[] raw = WorldSaveFixtures.SaveBytes(sim);
        ulong stateHash = sim.ComputeStateHash();

        // Контрольная загрузка: хэш после загрузки и продолжение обязаны совпасть с продолжением исходного мира.
        using Simulation loaded = WorldSaveFixtures.Load(raw);
        ulong afterLoad = loaded.ComputeStateHash();
        Assert.Equal(stateHash, afterLoad);
        sim.Step(ContinueTicks);
        loaded.Step(ContinueTicks);
        ulong afterContinue = sim.ComputeStateHash();
        Assert.Equal(afterContinue, loaded.ComputeStateHash());

        string stem = Path.Combine(dir, c.FileStem(Simulation.CurrentWorldVersion));
        File.WriteAllBytes(stem + ".tcsave", SaveEnvelope.Pack(raw));
        string json =
            "{\n" +
            $"  \"world_version\": {Simulation.CurrentWorldVersion},\n" +
            $"  \"tick\": {c.Ticks},\n" +
            $"  \"state_hash\": \"{stateHash:x16}\",\n" +
            $"  \"hash_after_load\": \"{afterLoad:x16}\",\n" +
            $"  \"continue_ticks\": {ContinueTicks},\n" +
            $"  \"hash_after_continue\": \"{afterContinue:x16}\"\n" +
            "}\n";
        File.WriteAllText(stem + ".json", json);
    }

    private static (ScenarioDesc, string[]) Small() => (WorldSaveFixtures.Small(), new[] { "car", "bus" });

    private static (ScenarioDesc, string[]) TerrainDemo() => (Sim.Core.Tests.Terrain.TerrainFixtures.Demo(), new[] { "car", "bus" });

    private static (ScenarioDesc, string[]) CrossProf() => (ProfileSaveFixtures.Cross(), new[] { "car" });

    private static (ScenarioDesc, string[]) S1kProf()
    {
        LoadedScenario sc = ProfileSaveFixtures.S1k();
        return (sc.Desc, sc.TypeNames);
    }

    private static (ScenarioDesc, string[]) SmallEdited() => (Sim.Core.Tests.Network.ToolStateFixture.Desc(), new[] { "car", "bus" });

    // Прогрев, дороги без connector'ов вдали от города строятся и освобождаются (дыры в пулах и арены), остаток тиков до ticks.
    private static void EvolveEdited(Simulation sim, int ticks)
    {
        sim.Step(ticks / 2);
        (int First, int Second) segs = Sim.Core.Tests.Network.ToolStateFixture.BuildDetached(sim);
        sim.Step(20);
        Sim.Core.Tests.Network.ToolStateFixture.ReleaseDetached(sim, segs);
        sim.Step(ticks - (ticks / 2) - 22);
    }

    // Снос самого загруженного сегмента за 25 тиков до конца: в сейве активная операция дренажа, закрытый сегмент, перестроенные маршруты.
    private static void EvolveDrain(Simulation sim, int ticks)
    {
        sim.Step(ticks - 25);
        int seg = Sim.Core.Tests.Tools.ToolsFixture.BusiestSegment(sim);
        var cmd = Command.MakeRemoveRoad(new RemoveRoadCmd { Seg = Sim.Core.Tests.Tools.ToolsFixture.Segment(sim, seg) }, 1);
        Assert.Equal(RejectReason.None, Sim.Core.Tests.Tools.ToolsFixture.Run(sim, in cmd));
        sim.Step(24);
        Assert.Equal(1, sim.World.Ops.Count);   // дренаж ещё идёт
    }

    private static (ScenarioDesc, string[]) S1k()
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k.json"));
        return (sc.Desc, sc.TypeNames);
    }

    private static (ScenarioDesc, string[]) S1kRes()
    {
        // Z2: сценарий s1k-res (жители, peds.source = city); тик 24000 - ночь позади, в пути машины и пешеходы, часть жителей в WaitDepart.
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res.json"));
        return (sc.Desc, sc.TypeNames);
    }

    private static (ScenarioDesc, string[]) S1kResProf()
    {
        LoadedScenario sc = ProfileSaveFixtures.Res();
        return (sc.Desc, sc.TypeNames);
    }

    private static (ScenarioDesc, string[]) S1kResEcon()
    {
        // E2: сценарий s1k-res-econ (s1k-res + economy.city_budget, содержание сети и автобусов).
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res-econ.json"));
        return (sc.Desc, sc.TypeNames);
    }

    private static (ScenarioDesc, string[]) S1kResPark()
    {
        // K1: s1k-res-park (parking.model = resource) плюс землепользование генератора: участки обходят парки и воду, в сейве блок LUSE.
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-res-park.json"));
        ScenarioDesc d = sc.Desc;
        d.City.LandUse = new LandUseDesc { Enabled = true, ParkShare = 0.08f, PondShare = 0.25f, Coast = LandUseCoast.South, CoastWidthM = 300f };
        return (d, sc.TypeNames);
    }

    private static (ScenarioDesc, string[]) S1kResEconProg()
    {
        LoadedScenario sc = Sim.Core.Tests.City.ProgressionFixtures.Econ();
        return (sc.Desc, sc.TypeNames);
    }

    // За 100 тиков до конца - SetBudgetPolicy: в сейве ожидающая политика (граница суток ещё впереди: тик 216500 > 216000, следующая - 288000).
    private static void EvolveBudget(Simulation sim, int ticks)
    {
        sim.Step(ticks - 100);
        BudgetPolicy p = default;
        p.TaxRBp = 800;
        p.TaxCBp = 1500;
        p.TaxIBp = 1500;
        p.LandTaxBpDay = 20;
        p.Fare = 3;
        p.Flags = BudgetPolicyFlags.Enable;
        sim.Enqueue(Command.MakeSetBudgetPolicy(in p, 1));
        sim.Step(100);
        Assert.True(sim.GetCityBudget(out CityBudgetInfo info));
        Assert.Equal(1, info.HasPending);
    }

    private static (ScenarioDesc, string[]) S1kPeds()
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k-peds.json"));
        return (sc.Desc, sc.TypeNames);
    }

    [Fact]
    public void Corpus_OldVersionFiles_AreKeptAndMigrate()
    {
        // Файлы w1 не удаляются и после миграций (1 -> 2 -> 3) продолжаются тем же хэшем, что записан в .json (хэш продолжения проверяет CheckFile для каждого файла).
        string[] old = Directory.GetFiles(CorpusDir, "w1-*.tcsave");
        Assert.True(old.Length >= 2, "корпус w1 (w1-small-t600, w1-s1k-t2400) должен остаться в репозитории");
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(1, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 0 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 1, rep.MigrationsApplied);
                Assert.False(sim!.HasPeds);
                Assert.False(sim.HasCity);
            }
        }
    }

    [Fact]
    public void Corpus_W2Files_AreKeptAndMigrate()
    {
        // Файлы w2 (пешеходы) остаются навсегда: миграция 2 -> 3 добавляет столбцы города с дефолтами, поведение и хэш продолжения не меняются.
        string[] old = Directory.GetFiles(CorpusDir, "w2-*.tcsave");
        Assert.True(old.Length >= 3, "корпус w2 (w2-small-t600, w2-s1k-t2400, w2-s1k-peds-t2400) должен остаться в репозитории");
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(2, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 0 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 2, rep.MigrationsApplied);
                Assert.Equal(path.Contains("-peds-", StringComparison.Ordinal), sim!.HasPeds);
                Assert.False(sim.HasCity);
            }
        }
    }

    [Fact]
    public void Corpus_W3Files_AreKeptAndMigrate()
    {
        // Файлы w3 (жители, зоны, экономика) остаются навсегда: миграция 3 -> 4 добавляет столбцы сети с дырами с дефолтами «правок не было», хэш продолжения не меняется
        // (его проверяет CheckFile для каждого файла).
        string[] old = Directory.GetFiles(CorpusDir, "w3-*.tcsave");
        Assert.True(old.Length >= 4, "корпус w3 (w3-small-t600, w3-s1k-t2400, w3-s1k-peds-t2400, w3-s1k-res-t24000) должен остаться в репозитории");
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(3, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 0 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 3, rep.MigrationsApplied);
                Assert.Equal(0, sim!.World.ToolsTouched);
                Assert.Null(sim.World.VerifyDerived());
            }
        }
    }

    [Fact]
    public void Corpus_W6Files_AreKeptAndMigrate_WithCurbBays()
    {
        // Файлы w6 (до карманов остановок, ADR-0029) остаются навсегда: миграция 6 -> 7 ставит bay_model = curb (остановки Bay ведут себя как раньше), хэш продолжения не меняется
        // (его проверяет CheckFile для каждого файла).
        string[] old = Directory.GetFiles(CorpusDir, "w6-*.tcsave");
        Assert.True(old.Length >= 7, "корпус w6 должен остаться в репозитории");
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(6, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 0 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 6, rep.MigrationsApplied);
                Assert.Equal(BayModel.Curb, sim!.World.TransitCfg.BayModel);
                Assert.Equal(60, sim.World.TransitCfg.BayYieldTicks);
                Assert.Equal(0, sim.Counters.BayEntries);
            }
        }
    }

    [Fact]
    public void Corpus_W7Files_AreKeptAndMigrate_ToLegacyProfiles()
    {
        // Файлы w7 (до профилей дорог, ADR-0030) остаются навсегда: миграция 7 -> 8 даёт режим legacy (Profile = -1, Width = NetLaneWidth, участки Orient = 255),
        // хэш продолжения не меняется (его проверяет CheckFile для каждого файла).
        string[] old = Directory.GetFiles(CorpusDir, "w7-*.tcsave");
        Assert.True(old.Length >= 7, "корпус w7 должен остаться в репозитории");
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(7, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 0 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 7, rep.MigrationsApplied);
                var net = sim!.World.Net;
                Assert.Null(net.Profiles);
                float laneWidth = sim.World.Builder.Options.LaneWidth;
                for (int s = 0; s < net.SegmentCount; s++)
                {
                    Assert.Equal(-1, net.Segments[s].Profile);
                }

                for (int l = 0; l < net.LaneCount; l++)
                {
                    float expect = net.LaneAlive(l) && net.Lanes[l].Kind == Sim.Core.Network.LaneKind.Normal ? laneWidth : 0f;
                    Assert.Equal(expect, net.Lanes[l].Width);
                }

                if (sim.World.City != null)
                {
                    for (int i = 0; i < sim.World.City.Lots.Count; i++)
                    {
                        Assert.Equal(Sim.Core.City.LotOrient.Legacy, sim.World.City.Lots.Orient[i]);
                    }
                }
            }
        }
    }

    [Fact]
    public void Corpus_W8Files_AreKeptAndMigrate_ToNoBudget()
    {
        // Файлы w8 (до казны города, ADR-0025) остаются навсегда: миграция 8 -> 9 даёт «бюджета нет» - дефолты параметров, потоки 13..16 и фонд оплаты нулевые,
        // хэш продолжения не меняется (его проверяет CheckFile для каждого файла).
        string[] old = Directory.GetFiles(CorpusDir, "w8-*.tcsave");
        Assert.True(old.Length >= 9, "корпус w8 должен остаться в репозитории");
        CityEconomyDesc def = CityDesc.Default().Economy;
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(8, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 0 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 8, rep.MigrationsApplied);
                Assert.False(sim!.GetCityBudget(out _));
                Assert.Equal(0, sim.Counters.FareEvaded + sim.Counters.DebtEvictions + sim.Counters.InsufficientFunds);
                Sim.Core.City.CityWorld? city = sim.World.City;
                if (city == null)
                {
                    Assert.False(sim.World.Town.Economy.CityBudget);
                    continue;
                }

                Assert.Null(city.Budget);
                CityEconomyDesc got = city.Cfg.Economy;
                Assert.False(got.CityBudget);
                Assert.Equal((def.TaxRBp, def.TaxCProfitBp, def.LandTaxBpDay, def.BusFare, def.CreditDays, def.LvRef, def.LvMax, def.VotPct), (got.TaxRBp, got.TaxCProfitBp, got.LandTaxBpDay, got.BusFare, got.CreditDays, got.LvRef, got.LvMax, got.VotPct));
                Assert.True(Sim.Core.City.CityBudget.IsValid(in got));
                for (int k = Sim.Core.City.Ledger.LegacyFlowCount; k < Sim.Core.City.Ledger.FlowCount; k++)
                {
                    Assert.Equal(0, city.Ledger.FlowDay[k] + city.Ledger.FlowYesterday[k] + city.Ledger.FlowTotal[k]);
                }

                for (int i = 0; i < city.Buildings.HighWater; i++)
                {
                    Assert.Equal(0, city.Buildings.PayrollEwma[i]);
                }
            }
        }
    }

    [Fact]
    public void Corpus_W9Files_AreKeptAndMigrate_ToNoParking()
    {
        // Файлы w9 (до парковки и землепользования в сейве, ADR-0026) остаются навсегда: миграция 9 -> 10 даёт «режима парковки нет, полигонов нет»,
        // хэш продолжения не меняется (его проверяет CheckFile для каждого файла).
        string[] old = Directory.GetFiles(CorpusDir, "w9-*.tcsave");
        Assert.True(old.Length >= 10, "корпус w9 должен остаться в репозитории");
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(9, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 0 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 9, rep.MigrationsApplied);
                Assert.False(sim!.World.ParkCfg.Enabled);
                Assert.Null(sim.World.City?.Parking);
                Assert.Empty(sim.LandUse.ToArray());
                Assert.Equal(0, sim.Counters.ParkCarShed + sim.Counters.ParkOverflow + sim.Counters.ParkSearchTimeCount);
                if (sim.World.City != null)
                {
                    Sim.Core.City.CityResidents r = sim.World.City.Residents;
                    for (int i = 0; i < r.HighWater; i++)
                    {
                        Assert.Equal((-1, -1, -1), (r.HomeFac[i], r.CarFac[i], r.CarLot[i]));
                    }
                }
            }
        }
    }

    [Fact]
    public void Corpus_W10Files_AreKeptAndMigrate_ToNoProgression()
    {
        // Файлы w10 (до прогрессии, ADR-0034) остаются навсегда: миграция 10 -> 11 даёт «прогрессии нет», пустое кольцо показателей у жителей (CKPI с отметками
        // текущих счётчиков), LockedRejects = 0; хэш продолжения не меняется (его проверяет CheckFile для каждого файла).
        string[] old = Directory.GetFiles(CorpusDir, "w10-*.tcsave");
        Assert.True(old.Length >= 12, "корпус w10 должен остаться в репозитории");
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(10, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 0 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 10, rep.MigrationsApplied);
                Assert.False(sim!.HasProgression);
                Assert.Equal(0, sim.Counters.LockedRejects);
                Sim.Core.City.CityWorld? city = sim.World.City;
                Assert.Equal(city != null, sim.GetCityKpi(out CityKpiInfo k));
                if (city != null)
                {
                    Assert.Equal(0, k.HoursFilled + k.DaysFilled);
                    Assert.Equal(sim.Counters.VehicleTicksTotal, city.Kpi.VehAtHourStart);
                    Assert.Equal(0, city.Budget?.WarnLevel ?? 0);
                }
            }
        }
    }

    [Fact]
    public void Corpus_W11Files_AreKeptAndMigrate_ToNoTerrain()
    {
        // Файлы w11 (до рельефа, ADR-0036) остаются навсегда: миграция 11 -> 12 даёт «рельефа нет» (PARM 241 = 0, 242-255 - дефолты), блока TERR и раздела хэша 14 нет,
        // хэш продолжения прежний (его проверяет CheckFile для каждого файла).
        string[] old = Directory.GetFiles(CorpusDir, "w11-*.tcsave");
        Assert.True(old.Length >= 13, "корпус w11 должен остаться в репозитории");
        foreach (string path in old)
        {
            byte[] raw = SaveEnvelope.ReadContainer(path);
            Assert.Equal(LoadStatus.Ok, Simulation.InspectSave(raw, out SaveHeaderInfo head));
            Assert.Equal(11, head.WorldVersion);
            var opt = new LoadOptions { StrictHash = 1 };
            Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in opt, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
            using (sim!)
            {
                Assert.Equal(Simulation.CurrentWorldVersion - 11, rep.MigrationsApplied);
                Assert.False(sim!.HasTerrain);
                Assert.Equal(head.StateHash, sim.ComputeStateHash());   // без рельефа раздел 14 не пишется: хэш мира w11 прежний
                Assert.True(Sim.Core.Terrain.TerrainParams.AreDefaults(sim.World.TerrainCfg));
            }
        }
    }

    [Fact]
    public void Corpus_TerrainDemo_HasTerrain()
    {
        string path = Path.Combine(CorpusDir, $"w{Simulation.CurrentWorldVersion}-terrain-demo-t600.tcsave");
        using Simulation sim = WorldSaveFixtures.Load(SaveEnvelope.ReadContainer(path));
        Assert.True(sim.HasTerrain);
        Assert.Equal(Sim.Core.Tests.Terrain.TerrainFixtures.DemoTerrain().BaseCm, sim.World.Terrain!.BaseCm);
    }

    private sealed record CorpusCase(string Name, int Ticks, Func<(ScenarioDesc, string[])> Build, Action<Simulation, int>? Evolve = null)
    {
        /// <summary>Пересоздаётся в текущей версии; иначе случай покрыт файлом версии <c>SubsetSinceVersion - 1</c>.</summary>
        public bool Current { get; init; }

        public string FileStem(int worldVersion) => $"w{worldVersion}-{Name}-t{Ticks}";
    }
}
