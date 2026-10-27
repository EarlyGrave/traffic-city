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
    private const long BudgetBytes = 10L * 1024 * 1024;   // 5 МБ на w1-w3; растёт на ~1,4 МБ с каждой версией (файлы старых версий не удаляются), решено в T4: 8 МБ; w6 (ADR-0028): 10 МБ
    private const long PedsFileBudgetBytes = 1024L * 1024;
    private const string RegenEnv = "TC_REGEN_CORPUS";

    private static readonly CorpusCase[] _cases =
    {
        new("small", 600, Small),
        new("s1k", 2400, S1k),
        new("s1k-peds", 2400, S1kPeds),
        new("s1k-res", 24000, S1kRes),
        new("s1k-res-d3", 216000, S1kRes),   // Z8 (E4): s1k-res на границе 3-х суток, настоящий календарь; тест Demand/ResCorpusTests
        new("small-drain", 600, Small, EvolveDrain),   // 4.3 (T4): сохранение посреди дренажа (операция сноса, закрытые полосы), w5
        new("small-edited", 600, SmallEdited, EvolveEdited),   // 4.3 (T2): сеть с дырами (ToolsTouched = 1), общие случаи w4 без дыр - остальные файлы
    };

    private static readonly Lazy<string> _dir = new(PrepareCorpusDir);

    private static string CorpusDir => _dir.Value;

    private static bool Regenerating => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(RegenEnv));

    [Fact]
    public void Corpus_HasCurrentVersion()
    {
        int v = Simulation.CurrentWorldVersion;
        foreach (CorpusCase c in _cases)
        {
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
        byte[] raw = SaveFile.ReadContainer(path);
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
            Generate(c, dir);
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
        File.WriteAllBytes(stem + ".tcsave", SaveFile.Pack(raw));
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
            byte[] raw = SaveFile.ReadContainer(path);
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
            byte[] raw = SaveFile.ReadContainer(path);
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
            byte[] raw = SaveFile.ReadContainer(path);
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

    private sealed record CorpusCase(string Name, int Ticks, Func<(ScenarioDesc, string[])> Build, Action<Simulation, int>? Evolve = null)
    {
        public string FileStem(int worldVersion) => $"w{worldVersion}-{Name}-t{Ticks}";
    }
}
