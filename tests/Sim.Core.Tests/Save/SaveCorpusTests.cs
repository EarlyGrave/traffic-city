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
/// повторный сейв побайтно совпадает. До этапа 3 корпус перегенерируется, а не мигрируется (решение владельца 2026-10-25):
/// <c>TC_REGEN_CORPUS=1 dotnet test tests/Sim.Core.Tests --filter FullyQualifiedName~SaveCorpusTests</c> пересоздаёт файлы текущей версии (старые удаляются).
/// </summary>
public sealed class SaveCorpusTests
{
    private const int ContinueTicks = 200;
    private const long BudgetBytes = 5L * 1024 * 1024;
    private const string RegenEnv = "TC_REGEN_CORPUS";

    private static readonly CorpusCase[] _cases =
    {
        new("small", 600, Small),
        new("s1k", 2400, S1k),
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
            if (current)
            {
                Assert.True(after == Hex(j, "hash_after_continue"), Regen($"{name}: хэш через {cont} тиков {after:x16} не равен записанному {Hex(j, "hash_after_continue"):x16} (поведение симуляции изменилось)"));
            }
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
        foreach (string old in Directory.GetFiles(dir, "w*-*").Where(f => f.EndsWith(".tcsave", StringComparison.Ordinal) || f.EndsWith(".json", StringComparison.Ordinal)))
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
        sim.Step(c.Ticks);

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

    private static (ScenarioDesc, string[]) S1k()
    {
        LoadedScenario sc = ScenarioLoader.LoadInfo(Path.Combine(GenFixture.RepoRoot(), "scenarios", "s1k.json"));
        return (sc.Desc, sc.TypeNames);
    }

    private sealed record CorpusCase(string Name, int Ticks, Func<(ScenarioDesc, string[])> Build)
    {
        public string FileStem(int worldVersion) => $"w{worldVersion}-{Name}-t{Ticks}";
    }
}
