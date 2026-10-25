using System;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Tests.Fixtures;
using Sim.Core.Tests.Worlds;
using Sim.Core.Util;

namespace Sim.Core.Tests.Api;

/// <summary>
/// <c>SetRenderView</c> (ADR-0019, sim-lod.md 13.7 A1): снапшот по виду совпадает с полным на слотах вида, вне вида <c>Flags = 0</c>,
/// хэш состояния от вида не зависит, смена вида между тиками, 0 B/тик (в т.ч. с воркерами).
/// Коллекция без параллельности: тест с воркерами меряет аллокации всего процесса.
/// </summary>
[Collection("WorkerThreads")]
public class RenderViewTests
{
    private static void Bounds(Simulation sim, out float minX, out float minZ, out float maxX, out float maxZ)
    {
        minX = minZ = float.MaxValue;
        maxX = maxZ = float.MinValue;
        foreach (NodeData n in sim.Network.Nodes)
        {
            minX = Math.Min(minX, n.Position.X);
            maxX = Math.Max(maxX, n.Position.X);
            minZ = Math.Min(minZ, n.Position.Z);
            maxZ = Math.Max(maxZ, n.Position.Z);
        }
    }

    /// <summary>Слот снапшота по виду либо нули, либо равен полному; машины внутри прямоугольника обязаны быть записаны.</summary>
    private static void Check(ReadOnlySpan<VehicleRenderData> full, ReadOnlySpan<VehicleRenderData> part, bool all, float x0, float z0, float x1, float z1, ref int written, ref int zeroed)
    {
        Assert.Equal(full.Length, part.Length);
        for (int i = 0; i < full.Length; i++)
        {
            VehicleRenderData f = full[i];
            VehicleRenderData p = part[i];
            if (p.Flags == 0)
            {
                Assert.Equal(default, p);
                if (f.Flags != 0)
                {
                    zeroed++;
                    Assert.False(all, "вид «всё» не должен обнулять живые слоты");
                    Assert.False(f.X >= x0 && f.X <= x1 && f.Z >= z0 && f.Z <= z1, $"машина {i} внутри вида ({f.X}, {f.Z}) не записана");
                }
            }
            else
            {
                Assert.Equal(f, p);
                written++;
            }
        }
    }

    [Fact]
    public void View_EqualsFullSnapshot_OnViewSlots_AndZeroOutside_AndHashIndependent_AcrossViewChanges()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation full = Simulation.Create(in d);
        using Simulation part = Simulation.Create(in d);
        Bounds(full, out float minX, out float minZ, out float maxX, out float maxZ);
        float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
        float hx = (maxX - minX) * 0.15f, hz = (maxZ - minZ) * 0.15f;

        // центр; левый угол; все; пустой вид вне сети; перевёрнутые границы; снова центр.
        var views = new (float X0, float Z0, float X1, float Z1, bool All)[]
        {
            (cx - hx, cz - hz, cx + hx, cz + hz, false),
            (minX, minZ, minX + (hx * 2), minZ + (hz * 2), false),
            (0, 0, 0, 0, true),
            (maxX + 5000, maxZ + 5000, maxX + 6000, maxZ + 6000, false),
            (cx + hx, cz + hz, cx - hx, cz - hz, false),
            (cx - hx, cz - hz, cx + hx, cz + hz, false),
        };

        int written = 0, zeroed = 0;
        full.Step(300);
        part.Step(300);
        foreach ((float x0, float z0, float x1, float z1, bool all) in views)
        {
            if (all)
            {
                part.SetRenderViewAll();
            }
            else
            {
                part.SetRenderView(x0, z0, x1, z1);
            }

            float lx0 = Math.Min(x0, x1), lx1 = Math.Max(x0, x1), lz0 = Math.Min(z0, z1), lz1 = Math.Max(z0, z1);
            for (int t = 0; t < 40; t++)
            {
                full.Step(1);
                part.Step(1);
                Assert.Equal(full.ComputeStateHash(), part.ComputeStateHash());
                Check(full.Vehicles, part.Vehicles, all, lx0, lz0, lx1, lz1, ref written, ref zeroed);
            }
        }

        Assert.True(written > 0, "вид ни разу не записал машин");
        Assert.True(zeroed > 0, "вид ни разу не отсёк машин: тест ничего не проверяет");
    }

    [Fact]
    public void ViewAll_AfterView_RestoresFullSnapshot()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation full = Simulation.Create(in d);
        using Simulation part = Simulation.Create(in d);
        Bounds(full, out float minX, out float minZ, out _, out _);
        part.SetRenderView(minX, minZ, minX + 1f, minZ + 1f);
        full.Step(200);
        part.Step(200);
        part.SetRenderViewAll();
        full.Step(2);
        part.Step(2);
        Assert.True(full.Vehicles.SequenceEqual(part.Vehicles));
        Assert.True(full.PreviousVehicles.Length > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void View_SnapshotEqualsSingleThread_AndStepAllocatesNothing(int threads)
    {
        ScenarioDesc d = Step3.S1k();
        using Simulation one = Simulation.Create(in d);
        using Simulation many = Simulation.Create(in d, new SimRuntimeOptions { WorkerThreads = threads });
        Bounds(one, out float minX, out float minZ, out float maxX, out float maxZ);
        float cx = (minX + maxX) * 0.5f, cz = (minZ + maxZ) * 0.5f;
        float hx = (maxX - minX) * 0.42f, hz = (maxZ - minZ) * 0.42f;
        one.SetRenderView(cx - hx, cz - hz, cx + hx, cz + hz);
        many.SetRenderView(cx - hx, cz - hz, cx + hx, cz + hz);
        one.Step(1500);
        many.Step(1500);
        Assert.True(many.World.Vehicles.HighWater > WorkerPool.ChunkSize, "должно быть больше одного чанка");
        Assert.Equal(one.ComputeStateHash(), many.ComputeStateHash());
        Assert.True(one.Vehicles.SequenceEqual(many.Vehicles), "снапшот по виду зависит от числа потоков");
        int visible = 0;
        foreach (VehicleRenderData r in many.Vehicles)
        {
            if (r.Flags != 0)
            {
                visible++;
            }
        }

        Assert.True(visible > WorkerPool.ChunkSize && visible < many.ActiveVehicleCount, $"вид должен захватывать часть машин: {visible} из {many.ActiveVehicleCount}");

        if (threads == 1)
        {
            long bytes = AllocationProbe.Measure(() => many.Step(100), out _);
            Assert.Equal(0, bytes);
        }
        else
        {
            // GetTotalAllocatedBytes видит и воркеров, но и чужие потоки: минимум по окнам.
            long best = long.MaxValue;
            for (int attempt = 0; attempt < 50 && best != 0; attempt++)
            {
                long a = GC.GetTotalAllocatedBytes(true);
                many.Step(100);
                long b = GC.GetTotalAllocatedBytes(true);
                best = Math.Min(best, b - a);
            }

            Assert.Equal(0, best);
        }
    }

    [Fact]
    public void NonFiniteView_MeansAll()
    {
        ScenarioDesc d = CityWorldTests.Desc("s1k");
        using Simulation full = Simulation.Create(in d);
        using Simulation part = Simulation.Create(in d);
        part.SetRenderView(0, 0, 1, 1);
        part.SetRenderView(float.NaN, 0, 1, 1);
        full.Step(300);
        part.Step(300);
        Assert.True(full.Vehicles.SequenceEqual(part.Vehicles));
    }
}
