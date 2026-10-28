using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Tests.City;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.Api;

/// <summary>RP10b: каталог профилей, <c>module_m</c>, начало сетки и клетки участков доступны клиенту через read API (без дескриптора), в том числе после Load; чтение без аллокаций.</summary>
public sealed class ViewApiProfileTests
{
    private static Simulation Make(in ScenarioDesc d) => Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = 1 });

    private static void AssertViewMatchesDesc(NetworkView v, in ScenarioDesc d)
    {
        RoadProfilesDesc cat = d.RoadProfiles;
        Assert.Equal(cat.ModuleMm, v.ModuleMm);
        Assert.Equal(d.WorldGridOriginX, v.GridOriginX);
        Assert.Equal(d.WorldGridOriginZ, v.GridOriginZ);
        Assert.Equal(cat.Profiles.Length, v.RoadProfiles.Length);
        int elems = 0;
        for (int p = 0; p < cat.Profiles.Length; p++)
        {
            RoadProfileDesc pd = cat.Profiles[p];
            RoadProfileData pv = v.RoadProfiles[p];
            Assert.Equal(pd.WidthMm, pv.WidthMm);
            Assert.Equal((byte)pd.Kind, pv.Kind);
            Assert.Equal(pd.Elements.Length, pv.ElemCount);
            Assert.Equal(elems, pv.FirstElem);
            int x = -(pd.WidthMm / 2);
            for (int e = 0; e < pd.Elements.Length; e++)
            {
                RoadElemData ev = v.ProfileElems[pv.FirstElem + e];
                Assert.Equal((byte)pd.Elements[e].Kind, ev.Kind);
                Assert.Equal((byte)pd.Elements[e].Dir, ev.Dir);
                Assert.Equal(pd.Elements[e].WidthMm, ev.WidthMm);
                Assert.Equal(x, ev.OffsetMm);
                x += pd.Elements[e].WidthMm;
            }

            elems += pd.Elements.Length;
        }

        Assert.Equal(elems, v.ProfileElems.Length);
    }

    [Fact]
    public void NetworkView_ExposesCatalogAndGrid_AndSurvivesSaveLoad()
    {
        ScenarioDesc d = ProfileSaveFixtures.Res().Desc;
        using Simulation sim = Make(in d);
        sim.Step(40);
        AssertViewMatchesDesc(sim.Network, in d);
        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim), 1);
        AssertViewMatchesDesc(loaded.Network, in d);   // каталог из RPRF, начало сетки из PARM - дескриптор загрузке не нужен
    }

    [Fact]
    public void NetworkView_Legacy_HasNoCatalog()
    {
        ScenarioDesc d = CityFixtures.S1kRes();
        using Simulation sim = Make(in d);
        NetworkView v = sim.Network;
        Assert.True(v.RoadProfiles.IsEmpty);
        Assert.True(v.ProfileElems.IsEmpty);
        Assert.Equal(0, v.ModuleMm);
        Assert.Equal(0f, v.GridOriginX);
        LotRenderData[] lots = new LotRenderData[sim.LotCount];
        Assert.True(sim.GetLots(lots) > 0);
        foreach (LotRenderData l in lots)
        {
            Assert.Equal(LotRenderData.OrientLegacy, l.Orient);
            Assert.False(l.OnGrid);
            Assert.False(l.TryGetCellBounds(out _, out _, out _, out _));
            Assert.Equal(0, l.CellsF + l.CellsD + l.CellX + l.CellZ);
        }
    }

    [Fact]
    public void GetLots_ReturnsCells_MatchingCoreBounds_AndSurvivesLoad()
    {
        ScenarioDesc d = ProfileSaveFixtures.Res().Desc;
        using Simulation sim = Make(in d);
        using Simulation loaded = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim), 1);
        foreach (Simulation s in new[] { sim, loaded })
        {
            LotStore store = s.World.City!.Lots;
            var lots = new LotRenderData[s.LotCount];
            Assert.Equal(store.Count, s.GetLots(lots));
            Assert.True(store.Count > 100);
            float mod = s.Network.ModuleMm * 0.001f;
            for (int i = 0; i < store.Count; i++)
            {
                LotRenderData l = lots[i];
                Assert.Equal(store.Orient[i], l.Orient);
                Assert.Equal(store.CellsF[i], l.CellsF);
                Assert.Equal(store.CellsD[i], l.CellsD);
                Assert.Equal(store.CellX[i], l.CellX);
                Assert.Equal(store.CellZ[i], l.CellZ);
                Assert.True(l.TryGetCellBounds(out int x0, out int z0, out int x1, out int z1));
                LotBuilder.CellBounds(store, i, out int cx0, out int cz0, out int cx1, out int cz1);
                Assert.Equal((cx0, cz0, cx1, cz1), (x0, z0, x1, z1));
                Assert.Equal(l.CellsF * l.CellsD, (x1 - x0 + 1) * (z1 - z0 + 1));
                // центр прямоугольника по сетке совпадает с центром участка из ядра
                float ox = s.Network.GridOriginX + ((x0 + x1 + 1) * mod * 0.5f);
                float oz = s.Network.GridOriginZ + ((z0 + z1 + 1) * mod * 0.5f);
                Assert.InRange(MathF.Abs(ox - l.X), 0f, 0.01f);
                Assert.InRange(MathF.Abs(oz - l.Z), 0f, 0.01f);
            }
        }
    }

    [Fact]
    public void ViewReads_AllocateNothing()
    {
        ScenarioDesc d = ProfileSaveFixtures.Res().Desc;
        using Simulation sim = Make(in d);
        var lots = new LotRenderData[sim.LotCount];
        long sink = 0;
        long bytes = AllocationProbe.Measure(() =>
        {
            NetworkView v = sim.Network;
            sink += v.ModuleMm + v.RoadProfiles.Length + v.ProfileElems.Length + (long)v.GridOriginX;
            sink += sim.GetLots(lots);
        }, out _);
        Assert.Equal(0, bytes);
        Assert.NotEqual(0, sink);
    }
}
