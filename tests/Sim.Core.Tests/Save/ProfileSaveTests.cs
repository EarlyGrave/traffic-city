using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Network;
using Sim.Core.Save;

namespace Sim.Core.Tests.Save;

/// <summary>RP2 (ADR-0030, road-profiles.md §3.4): сейв v8 хранит каталог профилей, <c>Segment.Profile</c>, <c>Mirrored</c>, <c>Lane.Width</c>, начало сетки и столбцы участков на сетке.</summary>
public class ProfileSaveTests
{
    private static Simulation Make(in ScenarioDesc d, int threads = 1)
    {
        CreateStatus st = Simulation.TryCreate(in d, new SimRuntimeOptions { WorkerThreads = threads }, out Simulation? sim);
        Assert.Equal(CreateStatus.Ok, st);
        return sim!;
    }

    private static Simulation LoadAt(byte[] bytes, int threads) => WorldSaveFixtures.Load(bytes, threads);

    private static void AssertSameProfileState(Simulation a, Simulation b)
    {
        Assert.Equal(a.ComputeStateHash(), b.ComputeStateHash());
        NetworkStore na = a.World.Net;
        NetworkStore nb = b.World.Net;
        Assert.Equal(na.Hash(), nb.Hash());
        Assert.NotNull(nb.Profiles);
        Assert.Equal(na.Profiles!.Count, nb.Profiles!.Count);
        Assert.Equal(na.Profiles.ModuleMm, nb.Profiles.ModuleMm);
        Assert.Equal(na.Profiles.Ids, nb.Profiles.Ids);
        Assert.Equal(na.Profiles.ElemKind, nb.Profiles.ElemKind);
        Assert.Equal(na.Profiles.ElemDir, nb.Profiles.ElemDir);
        Assert.Equal(na.Profiles.ElemWidthMm, nb.Profiles.ElemWidthMm);
        Assert.Equal(na.Profiles.LaneOffsetMm, nb.Profiles.LaneOffsetMm);
        Assert.Equal(na.Profiles.CurbLMm, nb.Profiles.CurbLMm);
        Assert.Equal(na.SegmentCount, nb.SegmentCount);
        for (int s = 0; s < na.SegmentCount; s++)
        {
            Assert.Equal(na.Segments[s].Profile, nb.Segments[s].Profile);
            Assert.Equal(na.Segments[s].Flags, nb.Segments[s].Flags);
        }

        for (int l = 0; l < na.LaneCount; l++)
        {
            Assert.Equal(na.Lanes[l].Width, nb.Lanes[l].Width);
        }

        Assert.Equal(a.World.WorldGridOriginX, b.World.WorldGridOriginX);
        Assert.Equal(a.World.WorldGridOriginZ, b.World.WorldGridOriginZ);
    }

    [Fact]
    public void Cross_ProfileMode_SurvivesSaveLoad_WithMirroredAndWidths()
    {
        ScenarioDesc d = ProfileSaveFixtures.Cross();
        using Simulation sim = Make(in d);
        sim.Step(600);
        Assert.Contains(sim.World.Net.Segments.AsSpan(0, sim.World.Net.SegmentCount).ToArray(), s => (s.Flags & SegmentFlags.Mirrored) != 0);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = LoadAt(bytes, 1);
        AssertSameProfileState(sim, loaded);
        Assert.Equal(-4f, loaded.World.WorldGridOriginX);
        Assert.Equal(12f, loaded.World.WorldGridOriginZ);
        Assert.True(bytes.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(loaded)));   // повторный сейв побайтно тот же
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void S1kProfiles_RoundTrip_SameHashAndContinuation_AtAnyThreadCount(int threads)
    {
        // Мир в режиме профилей не становится legacy после перезагрузки; хэш после загрузки и через 300 тиков равен хэшу непрерывного прогона, от числа потоков не зависит.
        ScenarioDesc d = ProfileSaveFixtures.S1k().Desc;
        using Simulation sim = Make(in d, threads);
        sim.Step(500);
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = LoadAt(bytes, threads);
        AssertSameProfileState(sim, loaded);
        sim.Step(300);
        loaded.Step(300);
        Assert.True(sim.ComputeStateHash() == loaded.ComputeStateHash(), "хэш после продолжения расходится");

        // и тот же хэш, что у однопоточного прогона
        using Simulation ref1 = Make(in d, 1);
        ref1.Step(800);
        Assert.Equal(ref1.ComputeStateHash(), loaded.ComputeStateHash());
    }

    [Fact]
    public void LegacyWorld_StaysLegacy_AfterSaveLoad()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        using Simulation sim = Make(in d);
        sim.Step(100);
        using Simulation loaded = LoadAt(WorldSaveFixtures.SaveBytes(sim), 1);
        Assert.Null(loaded.World.Net.Profiles);
        for (int s = 0; s < loaded.World.Net.SegmentCount; s++)
        {
            Assert.Equal(-1, loaded.World.Net.Segments[s].Profile);
        }
    }

    [Fact]
    public void ResidentsLots_GridColumns_RoundTrip_AndValidate()
    {
        // RP5: столбцы сетки живут только в мире с каталогом профилей (участки на сетке); legacy-мир с такими столбцами отклоняется.
        ScenarioDesc legacy = Sim.Core.Tests.City.CityFixtures.S1kRes();
        legacy.Routing.Levels = 0;
        using (Simulation ls = Make(in legacy))
        {
            ls.Step(50);
            LotStore ll = ls.World.City!.Lots;
            Assert.All(ll.Orient.AsSpan(0, ll.Count).ToArray(), o => Assert.Equal(LotOrient.Legacy, o));
            ll.CellsF[1] = 3;
            ll.CellsD[1] = 2;
            ll.Orient[1] = 2;
            AssertRejected(WorldSaveFixtures.SaveBytes(ls));
        }

        ScenarioDesc d = ProfileSaveFixtures.Res().Desc;
        d.Routing.Levels = 0;
        using Simulation sim = Make(in d);
        sim.Step(50);
        LotStore l = sim.World.City!.Lots;
        Assert.True(l.Count >= 3);
        Assert.All(l.Orient.AsSpan(0, l.Count).ToArray(), o => Assert.NotEqual(LotOrient.Legacy, o));
        l.CellX[1] = -7;
        l.CellZ[1] = 12;
        l.CellsF[1] = 3;
        l.CellsD[1] = 2;
        l.Orient[1] = 2;
        byte[] bytes = WorldSaveFixtures.SaveBytes(sim);
        using Simulation loaded = LoadAt(bytes, 1);
        LotStore m = loaded.World.City!.Lots;
        Assert.Equal(l.Count, m.Count);
        Assert.Equal(l.CellX.AsSpan(0, l.Count).ToArray(), m.CellX.AsSpan(0, l.Count).ToArray());
        Assert.Equal(l.CellZ.AsSpan(0, l.Count).ToArray(), m.CellZ.AsSpan(0, l.Count).ToArray());
        Assert.Equal(l.CellsF.AsSpan(0, l.Count).ToArray(), m.CellsF.AsSpan(0, l.Count).ToArray());
        Assert.Equal(l.CellsD.AsSpan(0, l.Count).ToArray(), m.CellsD.AsSpan(0, l.Count).ToArray());
        Assert.Equal(l.Orient.AsSpan(0, l.Count).ToArray(), m.Orient.AsSpan(0, l.Count).ToArray());
        Assert.Equal((byte)2, m.Orient[1]);

        // Сочетания вне §4.4 отклоняются: ось 7; клетки у legacy-участка; участок на сетке без размера.
        AssertRejected(Patch<byte>(bytes, "LOTS", 15, (_, a) => a[1] = 7));
        AssertRejected(Patch<byte>(bytes, "LOTS", 15, (_, a) => a[0] = LotOrient.Legacy));   // Orient = 255 при ненулевых клетках
        AssertRejected(Patch<byte>(bytes, "LOTS", 14, (_, a) => a[1] = 0));
    }

    // ---- валидатор ----

    private static byte[] CrossSave()
    {
        ScenarioDesc d = ProfileSaveFixtures.Cross();
        using Simulation sim = Make(in d);
        sim.Step(100);
        return WorldSaveFixtures.SaveBytes(sim);
    }

    private static byte[] Patch<T>(byte[] file, string block, uint columnId, Action<SaveTables, Span<T>> edit)
        where T : unmanaged
    {
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(file, null, out SaveTables? t, out _));
        int b = t!.FindBlock(SaveFormat.FourCc(System.Text.Encoding.ASCII.GetBytes(block)));
        Assert.True(b >= 0);
        Assert.True(t.TryGetColumn(b, columnId, out ReadOnlySpan<T> col));
        edit(t, MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(col), col.Length));
        SaveHeaderInfo h = t.Header;
        Assert.Equal(SaveStatus.Ok, SaveWriter.Measure(ref t, out long len));
        byte[] buf = new byte[len];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, buf, out _));
        return buf;
    }

    private static void AssertRejected(byte[] bad)
    {
        LoadStatus st = LoadStatus.Ok;
        Exception? ex = Record.Exception(() => st = Simulation.Load(bad, default, default, out _, out _));
        Assert.Null(ex);
        Assert.Equal(LoadStatus.InvalidValue, st);
    }

    [Fact]
    public void Control_RewriteWithoutCorruption_Loads()
    {
        byte[] same = Patch<int>(CrossSave(), "NSEG", 16, (_, _) => { });
        Assert.Equal(LoadStatus.Ok, Simulation.Load(same, default, default, out Simulation? s, out _));
        s!.Dispose();
    }

    [Fact]
    public void Validator_RejectsBadProfileIndex_AndMirroredWithoutProfile()
    {
        byte[] bytes = CrossSave();
        AssertRejected(Patch<int>(bytes, "NSEG", 16, (_, p) => p[0] = 99));    // индекс вне каталога
        AssertRejected(Patch<int>(bytes, "NSEG", 16, (_, p) => p[0] = -2));
        AssertRejected(Patch<int>(bytes, "NSEG", 16, (_, p) => p[3] = -1));    // Mirrored без профиля
        AssertRejected(Patch<int>(bytes, "NSEG", 16, (_, p) => p[0] = p[2]));  // число полос не из профиля
    }

    [Fact]
    public void Validator_RejectsLaneWidthNotFromProfile_AndNegative()
    {
        byte[] bytes = CrossSave();
        AssertRejected(Patch<float>(bytes, "NLAN", 19, (_, w) => w[0] += 0.01f));
        AssertRejected(Patch<float>(bytes, "NLAN", 19, (_, w) => w[0] = -1f));
        AssertRejected(Patch<float>(bytes, "NLAN", 19, (_, w) => w[0] = float.NaN));
    }

    [Fact]
    public void Validator_RejectsBrokenCatalog_AndModeMismatch()
    {
        byte[] bytes = CrossSave();
        AssertRejected(Patch<int>(bytes, "RPRF", 3, (_, w) => w[0] += 1000));       // ширина профиля не кратна модулю
        AssertRejected(Patch<int>(bytes, "RPRF", 1, (_, m) => m[0] = 0));           // модуль 0
        AssertRejected(Patch<byte>(bytes, "RPRF", 4, (_, c) => c[0] = (byte)(c[0] + 1)));   // число элементов не сходится с суммой
        AssertRejected(Patch<int>(bytes, "PARM", 188, (_, m) => m[0] = 0));         // каталог есть, режим legacy
        AssertRejected(Patch<int>(bytes, "PARM", 188, (_, m) => m[0] = 2));
    }

    [Fact]
    public void LegacySave_WithProfileMode_AndNoCatalog_IsMissingBlock()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        using Simulation sim = Make(in d);
        byte[] bytes = Patch<int>(WorldSaveFixtures.SaveBytes(sim), "PARM", 188, (_, m) => m[0] = 1);
        LoadStatus st = Simulation.Load(bytes, default, default, out _, out _);
        Assert.Equal(LoadStatus.MissingBlock, st);
    }
}
