using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Save;
using Sim.Core.Tests.Gen;

namespace Sim.Core.Tests.Save;

/// <summary>Реестр столбцов: полнота относительно структур состояния и совпадение с приложением A save-format.md.</summary>
public class SaveSchemaTests
{
    [Fact]
    public void Registry_HasUniqueIdsAndAscendingColumns()
    {
        var blocks = new HashSet<uint>();
        foreach (BlockDef b in SaveSchema.Blocks)
        {
            Assert.True(blocks.Add(b.Id), b.Name);
            uint prev = 0;
            foreach (ColumnDef c in b.Columns)
            {
                Assert.True(c.Id > prev, $"{b.Name}.{c.Name}");
                prev = c.Id;
            }
        }
    }

    private static HashSet<string> ColumnNames(string block)
    {
        BlockDef b = Array.Find(SaveSchema.Blocks, x => x.Name == block)!;
        var set = new HashSet<string>();
        foreach (ColumnDef c in b.Columns)
        {
            set.Add(c.Name);
        }

        return set;
    }

    [Fact]
    public void EveryCounterField_HasAColumn()
    {
        // Новый счётчик SimCounters без столбца = молча потерянное поле сейва: тест красный, пока не добавлен столбец и не поднята world_version.
        HashSet<string> names = ColumnNames("CNTR");
        foreach (FieldInfo f in typeof(SimCounters).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            Assert.Contains(f.Name, names);
        }

        Assert.Equal(typeof(SimCounters).GetFields(BindingFlags.Public | BindingFlags.Instance).Length, names.Count);
    }

    private static void AllFieldsCovered(Type t, string block, string prefix, params string[] skip)
    {
        HashSet<string> names = ColumnNames(block);
        foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (Array.IndexOf(skip, f.Name) >= 0)
            {
                continue;
            }

            bool vec = f.FieldType == typeof(Sim.Core.Util.Vec2);
            if (vec)
            {
                Assert.Contains(prefix + f.Name + "X", names);
                Assert.Contains(prefix + f.Name + "Z", names);
            }
            else
            {
                Assert.Contains(prefix + f.Name, names);
            }
        }
    }

    [Fact]
    public void NetworkStructs_AreFullyCovered()
    {
        AllFieldsCovered(typeof(NodeData), "NNOD", string.Empty);
        AllFieldsCovered(typeof(SegmentData), "NSEG", string.Empty, "Profile");   // Profile и Lane.Width (ADR-0030) в сейв попадут в RP2 (world_version 8)
        AllFieldsCovered(typeof(LaneData), "NLAN", string.Empty, "Width");
        AllFieldsCovered(typeof(ZoneData), "NZON", string.Empty, "Reserved0", "Reserved1", "Reserved2");
    }

    [Fact]
    public void Appendix_CanBeDumped()
    {
        // TC_DUMP_SCHEMA=<файл>: записать таблицу приложения A (вставляется в save-format.md между schema:begin/end).
        string? target = Environment.GetEnvironmentVariable("TC_DUMP_SCHEMA");
        if (!string.IsNullOrEmpty(target))
        {
            File.WriteAllText(target, SaveSchema.AppendixMarkdown());
        }
    }

    [Fact]
    public void EveryDoc_Appendix_MatchesRegistry()
    {
        string path = Path.Combine(GenFixture.RepoRoot(), "docs", "design", "save-format.md");
        string doc = File.ReadAllText(path).Replace("\r\n", "\n");
        const string marker = "<!-- schema:begin -->\n";
        int a = doc.IndexOf(marker, StringComparison.Ordinal);
        int b = doc.IndexOf("<!-- schema:end -->", StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, "save-format.md: нет разметки schema:begin/end приложения A");
        string table = doc.Substring(a + marker.Length, b - a - marker.Length);
        Assert.Equal(SaveSchema.AppendixMarkdown(), table);
    }
}
