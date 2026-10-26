#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Tests.Gen;
using Sim.Headless;

namespace Sim.Core.Tests.Save;

/// <summary>Миграция <c>world_version</c> 2 -> 3 (save-format.md §4, §12; zoning-demand.md §15): столбцы города с дефолтами «города нет», новых блоков нет, поведение и хэш прежние.</summary>
public sealed class CityMigrationTests
{
    private static SaveTables ReadCorpus(string name)
    {
        byte[] raw = SaveFile.ReadContainer(Path.Combine(GenFixture.RepoRoot(), "tests", "SaveCorpus", name));
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(raw, SaveSchema.KnownBlockIds(), out SaveTables? t, out _));
        return t!;
    }

    [Theory]
    [InlineData("w2-small-t600.tcsave")]
    [InlineData("w2-s1k-t2400.tcsave")]
    [InlineData("w2-s1k-peds-t2400.tcsave")]
    public void W2Tables_AfterStep_HaveExactlyTheV3ColumnsOfOldBlocks(string file)
    {
        SaveTables t = ReadCorpus(file);
        Assert.Equal(2, t.Header.WorldVersion);
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(t, 2, 3, Migrations.Steps, out int applied));
        Assert.Equal(1, applied);
        Assert.Equal(3, t.Header.WorldVersion);
        foreach (BlockDef b in SaveSchema.Blocks)
        {
            int idx = t.FindBlock(b.Id);
            if (idx < 0)
            {
                Assert.True(b.PresentText != "всегда", $"нет обязательного блока {b.Name}");
                continue;
            }

            Assert.Equal(b.Columns.Length, t.ColumnCountOf(idx));
            foreach (ColumnDef c in b.Columns)
            {
                Assert.True(t.FindColumn(idx, c.Id) >= 0, $"{b.Name}.{c.Name} (id {c.Id}) отсутствует после миграции");
            }
        }

        foreach (string name in new[] { "BTYP", "LOTS", "BLDG", "RESD", "SKIM", "LEDG" })
        {
            Assert.True(t.FindBlock(SaveFormat.FourCc(System.Text.Encoding.ASCII.GetBytes(name))) < 0, name);
        }
    }

    [Fact]
    public void MigratedColumns_HaveTheDefaults()
    {
        SaveTables t = ReadCorpus("w2-s1k-t2400.tcsave");
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(t, 2, 3, Migrations.Steps, out _));
        int parm = t.FindBlock(SaveFormat.FourCc("PARM"u8));
        int veh = t.FindBlock(SaveFormat.FourCc("VEHC"u8));
        for (uint id = 113; id <= 184; id++)
        {
            int ci = t.FindColumn(parm, id);
            Assert.True(ci >= 0, $"PARM {id}");
            SaveColumnDesc d = t.GetColumn(parm, ci);
            Assert.Equal(1, d.Count);
            t.TryGetColumnBytes(parm, id, out ReadOnlySpan<byte> bytes);
            Assert.True(bytes.IndexOfAnyExcept((byte)0) < 0, $"PARM {id} не ноль");
        }

        Assert.True(t.TryGetColumn(veh, 1, out ReadOnlySpan<int> hw));
        Assert.True(t.TryGetColumn(veh, 41, out ReadOnlySpan<int> resident));
        Assert.Equal(hw[0], resident.Length);
        Assert.True(resident.Length > 0);
        foreach (int v in resident)
        {
            Assert.Equal(-1, v);
        }
    }

    [Fact]
    public void Migration_IsPure_SameInputSameBytes()
    {
        SaveTables a = ReadCorpus("w2-small-t600.tcsave");
        SaveTables b = ReadCorpus("w2-small-t600.tcsave");
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(a, 2, 3, Migrations.Steps, out _));
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(b, 2, 3, Migrations.Steps, out _));
        SaveHeaderInfo ha = a.Header;
        long na = SaveWriter.Measure(ref a, out long la) == SaveStatus.Ok ? la : -1;
        long nb = SaveWriter.Measure(ref b, out long lb) == SaveStatus.Ok ? lb : -1;
        Assert.Equal(na, nb);
        var fa = new byte[na];
        var fb = new byte[nb];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref a, ha, fa, out _));
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref b, b.Header, fb, out _));
        Assert.True(fa.AsSpan().SequenceEqual(fb));
    }

    [Fact]
    public void MigratedW2Peds_LoadsWithPeds_AndContinuesWithSameHash()
    {
        string stem = Path.Combine(GenFixture.RepoRoot(), "tests", "SaveCorpus", "w2-s1k-peds-t2400");
        byte[] raw = SaveFile.ReadContainer(stem + ".tcsave");
        string json = File.ReadAllText(stem + ".json");
        ulong expected = ulong.Parse(System.Text.RegularExpressions.Regex.Match(json, "\"hash_after_continue\": \"([0-9a-f]+)\"").Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
        var o = new LoadOptions { StrictHash = 0 };
        Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
        using (sim!)
        {
            Assert.Equal(2, rep.FileWorldVersion);
            Assert.Equal(1, rep.MigrationsApplied);
            Assert.True(sim!.HasPeds);
            Assert.False(sim.HasCity);
            Assert.Equal(PedSource.Preform, sim.World.PedCfg.Source);
            sim.Step(200);
            Assert.Equal(expected, sim.ComputeStateHash());
        }
    }
}
