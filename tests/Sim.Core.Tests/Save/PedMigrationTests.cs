#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using System.IO;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Tests.Gen;
using Sim.Headless;
using Sim.Host;

namespace Sim.Core.Tests.Save;

/// <summary>Миграции w1 -> w2 (пешеходы, save-format.md §4, pedestrians.md §11) и дальше по цепочке: чистые функции над таблицами, результат - ровно текущая схема без блоков пешеходов и города.</summary>
public sealed class PedMigrationTests
{
    private static SaveTables ReadCorpus(string name)
    {
        byte[] raw = SaveEnvelope.ReadContainer(Path.Combine(GenFixture.RepoRoot(), "tests", "SaveCorpus", name));
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(raw, SaveSchema.KnownBlockIds(), out SaveTables? t, out _));
        return t!;
    }

    [Theory(Skip = "режим прототипа: миграций сейвов нет (CLAUDE.md)")]
    [InlineData("w1-small-t600.tcsave")]
    [InlineData("w1-s1k-t2400.tcsave")]
    public void W1Tables_AfterStep_HaveExactlyTheV2ColumnsOfOldBlocks(string file)
    {
        SaveTables t = ReadCorpus(file);
        Assert.Equal(1, t.Header.WorldVersion);
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(t, 1, SaveSchema.WorldVersion, Migrations.Steps, out int applied));
        Assert.Equal(SaveSchema.WorldVersion - 1, applied);
        Assert.Equal(SaveSchema.WorldVersion, t.Header.WorldVersion);
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

        // Блоков пешеходов у старого мира нет.
        foreach (string name in new[] { "PEDS", "PPTH", "RESI", "XING", "PQUE", "BTYP", "LOTS", "BLDG", "RESD", "SKIM", "LEDG" })
        {
            Assert.True(t.FindBlock(SaveFormat.FourCc(System.Text.Encoding.ASCII.GetBytes(name))) < 0, name);
        }
    }

    [Fact]
    public void Migration_IsPure_SameInputSameBytes()
    {
        SaveTables a = ReadCorpus("w1-small-t600.tcsave");
        SaveTables b = ReadCorpus("w1-small-t600.tcsave");
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(a, 1, 2, Migrations.Steps, out _));
        Assert.Equal(LoadStatus.Ok, Migrations.Apply(b, 1, 2, Migrations.Steps, out _));
        SaveHeaderInfo ha = a.Header;
        SaveHeaderInfo hb = b.Header;
        long na = SaveWriter.Measure(ref a, out long la) == SaveStatus.Ok ? la : -1;
        long nb = SaveWriter.Measure(ref b, out long lb) == SaveStatus.Ok ? lb : -1;
        Assert.Equal(na, nb);
        var fa = new byte[na];
        var fb = new byte[nb];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref a, ha, fa, out _));
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref b, hb, fb, out _));
        Assert.True(fa.AsSpan().SequenceEqual(fb));
    }

    [Fact(Skip = "режим прототипа: миграций сейвов нет (CLAUDE.md)")]
    public void MigratedS1k_LoadsAsWorldWithoutPeds_AndContinuesWithSameHash()
    {
        string stem = Path.Combine(GenFixture.RepoRoot(), "tests", "SaveCorpus", "w1-s1k-t2400");
        byte[] raw = SaveEnvelope.ReadContainer(stem + ".tcsave");
        string json = File.ReadAllText(stem + ".json");
        ulong expected = ulong.Parse(System.Text.RegularExpressions.Regex.Match(json, "\"hash_after_continue\": \"([0-9a-f]+)\"").Groups[1].Value, System.Globalization.NumberStyles.HexNumber);
        var o = new LoadOptions { StrictHash = 0 };
        Assert.Equal(LoadStatus.Ok, Simulation.Load(raw, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? sim, out LoadReport rep));
        using (sim!)
        {
            Assert.Equal(1, rep.FileWorldVersion);
            Assert.Equal(SaveSchema.WorldVersion - 1, rep.MigrationsApplied);
            Assert.False(sim!.HasPeds);
            Assert.False(sim.HasCity);
            Assert.True(sim.Pedestrians.IsEmpty);
            sim.Step(200);
            Assert.Equal(expected, sim.ComputeStateHash());
        }
    }
}
