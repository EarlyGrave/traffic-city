#pragma warning disable CA1861 // тест: литералы-массивы допустимы
using System;
using Sim.Core.Api;
using Sim.Scenarios;

namespace Sim.Core.Tests.Save;

/// <summary>Манифест ассетов <c>MANI</c> (save-format.md §5): хранение, round-trip, отчёт о недостающих и изменившихся, заглушка = продолжение по параметрам из сейва.</summary>
public sealed class AssetManifestTests
{
    private static readonly string[] _names = { "car", "bus" };

    private static Simulation NewSim(out ScenarioDesc desc, bool manifest = true)
    {
        desc = WorldSaveFixtures.Small();
        Simulation sim = Simulation.Create(desc, new SimRuntimeOptions { WorkerThreads = 1 });
        if (manifest)
        {
            AssetManifestBuilder.ForVehicleTypes(_names, desc.VehicleTypes, out AssetRef[] refs, out byte[] ids);
            Assert.True(sim.SetAssetManifest(refs, ids));
        }

        return sim;
    }

    [Fact]
    public void Manifest_RoundTrips_AndResaveIsByteEqual()
    {
        using Simulation sim = NewSim(out _);
        sim.Step(60);
        byte[] file = WorldSaveFixtures.SaveBytes(sim);

        using Simulation back = WorldSaveFixtures.Load(file);
        Assert.Equal(2, back.AssetManifest.Length);
        Assert.True(back.AssetManifest.SequenceEqual(sim.AssetManifest));
        Assert.True(back.AssetIds.SequenceEqual(sim.AssetIds));
        Assert.Equal("vehicle:car", System.Text.Encoding.UTF8.GetString(back.AssetIds.Slice((int)back.AssetManifest[0].IdOffset, (int)back.AssetManifest[0].IdLength)));
        Assert.Equal((byte)AssetKind.VehicleType, back.AssetManifest[1].Kind);
        Assert.True(file.AsSpan().SequenceEqual(WorldSaveFixtures.SaveBytes(back)));
    }

    [Fact]
    public void Manifest_IsNotPartOfTheStateHash()
    {
        using Simulation with = NewSim(out _);
        using Simulation without = NewSim(out _, manifest: false);
        with.Step(40);
        without.Step(40);
        Assert.Equal(without.ComputeStateHash(), with.ComputeStateHash());
    }

    [Fact]
    public void NoManifest_SavesEmptyBlock_AndLoads()
    {
        using Simulation sim = NewSim(out _, manifest: false);
        sim.Step(10);
        using Simulation back = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(sim));
        Assert.Equal(0, back.AssetManifest.Length);
    }

    [Fact]
    public void Load_WithoutCatalog_ReportsNothing()
    {
        using Simulation sim = NewSim(out _);
        byte[] file = WorldSaveFixtures.SaveBytes(sim);
        var o = new LoadOptions { StrictHash = 1 };
        Assert.Equal(LoadStatus.Ok, Simulation.Load(file, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? back, out LoadReport rep));
        using (back!)
        {
            Assert.Equal(2, rep.AssetsInManifest);
            Assert.True(rep.AssetsMissing == 0 && rep.AssetsChanged == 0);
            Assert.True(back!.AssetStates.ToArray().AsSpan().IndexOfAnyExcept((byte)0) < 0);
        }
    }

    [Fact]
    public void MissingAndChangedAssets_AreReported_AndSimulationContinuesOnSavedParameters()
    {
        using Simulation sim = NewSim(out ScenarioDesc desc);
        sim.Step(100);
        byte[] file = WorldSaveFixtures.SaveBytes(sim);

        // Каталог хоста сейчас: машина стала другой (ContentHash иной), автобуса нет (снят мод).
        VehicleTypeDesc[] now = (VehicleTypeDesc[])desc.VehicleTypes.Clone();
        now[0].Accel *= 2f;
        AssetManifestBuilder.ForVehicleTypes(_names, now, out AssetRef[] cat, out byte[] catIds);
        var o = new LoadOptions { StrictHash = 1, AvailableAssets = new[] { cat[0] }, AvailableIds = catIds };
        Assert.Equal(LoadStatus.Ok, Simulation.Load(file, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? back, out LoadReport rep));
        using (back!)
        {
            Assert.Equal(2, rep.AssetsInManifest);
            Assert.Equal(1, rep.AssetsMissing);
            Assert.Equal(1, rep.AssetsChanged);
            Assert.Equal(new[] { (byte)AssetState.Changed, (byte)AssetState.Missing }, back!.AssetStates.ToArray());

            // Заглушке нужны габариты из сейва; параметры - прежние, поэтому продолжение побитовое.
            Assert.Equal(desc.VehicleTypes[1].Length, back.VehicleTypes[1].Length);
            Assert.Equal(desc.VehicleTypes[0].Accel, back.VehicleTypes[0].Accel);
            sim.Step(80);
            back.Step(80);
            Assert.Equal(sim.ComputeStateHash(), back.ComputeStateHash());

            // Запись манифеста пересохраняется (вернули мод - вернулся визуал): тихой подмены нет.
            using Simulation again = WorldSaveFixtures.Load(WorldSaveFixtures.SaveBytes(back));
            Assert.True(again.AssetManifest.SequenceEqual(sim.AssetManifest));
        }
    }

    [Fact]
    public void SetAssetManifest_RejectsContradictoryEntries_AndKeepsTheOldOne()
    {
        using Simulation sim = NewSim(out _);
        ReadOnlySpan<AssetRef> good = sim.AssetManifest;
        byte[] ids = sim.AssetIds.ToArray();

        AssetRef badKind = good[0];
        badKind.Kind = 9;
        AssetRef badSlice = good[0];
        badSlice.IdLength = (uint)ids.Length + 1;
        AssetRef badType = good[0];
        badType.SavedIndex = 5;
        AssetRef badReserved = good[0];
        badReserved.R1 = 1;

        foreach (AssetRef a in new[] { badKind, badSlice, badType, badReserved })
        {
            Assert.False(sim.SetAssetManifest(new[] { a }, ids));
        }

        Assert.Equal(2, sim.AssetManifest.Length);
    }

    [Fact]
    public void Load_RejectsCatalogThatContradictsItself()
    {
        using Simulation sim = NewSim(out _);
        byte[] file = WorldSaveFixtures.SaveBytes(sim);
        var o = new LoadOptions { AvailableAssets = new[] { new AssetRef { Kind = 1, IdLength = 99 } }, AvailableIds = new byte[1] };
        Assert.Equal(LoadStatus.InvalidValue, Simulation.Load(file, in o, new SimRuntimeOptions { WorkerThreads = 1 }, out Simulation? back, out _));
        Assert.Null(back);
    }
}
