using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Network;
using Sim.Core.Util;

namespace Sim.Core.Tests.Api;

public class LayoutTests
{
    public static TheoryData<Type, int> FixedSizes => new()
    {
        { typeof(Command), 64 },
        { typeof(BuildRoadCmd), 24 },
        { typeof(SpawnTripCmd), 24 },
        { typeof(SetSignalPlanCmd), 32 },
        { typeof(CreateBusRouteCmd), 12 },
        { typeof(AppendBusStopCmd), 16 },
        { typeof(SpawnBusCmd), 4 },
        { typeof(SelectVehicleCmd), 8 },
        { typeof(SetTargetVehicleCountCmd), 4 },
        { typeof(SetNodeControlCmd), 8 },
        { typeof(VehicleRenderData), 32 },
        { typeof(SegmentLoad), 12 },
        { typeof(SignalState), 8 },
        { typeof(RouteStep), 8 },
        { typeof(SimEvent), 24 },
        { typeof(VehicleHandle), 8 },
        { typeof(NodeHandle), 8 },
        { typeof(SegmentHandle), 8 },
        { typeof(LaneHandle), 8 },
        { typeof(ZoneHandle), 8 },
        { typeof(BusRouteHandle), 8 },
        { typeof(BusStopHandle), 8 },
        { typeof(RouteHandle), 8 },
        { typeof(Vec2), 8 },
    };

    [Theory]
    [MemberData(nameof(FixedSizes))]
    public void Size_MatchesSpec_ForUnsafeAndMarshal(Type type, int size)
    {
        Assert.Equal(size, SizeOfUnsafe(type));
        Assert.Equal(size, Marshal.SizeOf(type));
    }

    private static int SizeOfUnsafe(Type t) =>
        (int)typeof(LayoutTests).GetMethod(nameof(SizeOfGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(t).Invoke(null, null)!;

    private static int SizeOfGeneric<T>() => Unsafe.SizeOf<T>();

    [Fact]
    public void Command_PayloadOffsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<Command>(nameof(Command.Kind)));
        Assert.Equal(4, (int)Marshal.OffsetOf<Command>(nameof(Command.Tag)));
        foreach (string f in new[]
        {
            nameof(Command.BuildRoad), nameof(Command.SpawnTrip), nameof(Command.SetSignalPlan),
            nameof(Command.CreateBusRoute), nameof(Command.AppendBusStop), nameof(Command.SpawnBus),
            nameof(Command.SelectVehicle), nameof(Command.SetTargetVehicleCount), nameof(Command.SetNodeControl),
        })
        {
            Assert.Equal(8, (int)Marshal.OffsetOf<Command>(f));
        }
    }

    [Fact]
    public void VehicleRenderData_FieldOffsets()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<VehicleRenderData>(nameof(VehicleRenderData.X)));
        Assert.Equal(12, (int)Marshal.OffsetOf<VehicleRenderData>(nameof(VehicleRenderData.Heading)));
        Assert.Equal(20, (int)Marshal.OffsetOf<VehicleRenderData>(nameof(VehicleRenderData.Index)));
        Assert.Equal(24, (int)Marshal.OffsetOf<VehicleRenderData>(nameof(VehicleRenderData.Generation)));
        Assert.Equal(26, (int)Marshal.OffsetOf<VehicleRenderData>(nameof(VehicleRenderData.Type)));
        Assert.Equal(27, (int)Marshal.OffsetOf<VehicleRenderData>(nameof(VehicleRenderData.Flags)));
        Assert.Equal(28, (int)Marshal.OffsetOf<VehicleRenderData>(nameof(VehicleRenderData.LateralOffset)));
    }

    [Fact]
    public void SimEvent_FieldOffsets()
    {
        Assert.Equal(8, (int)Marshal.OffsetOf<SimEvent>(nameof(SimEvent.Tick)));
        Assert.Equal(16, (int)Marshal.OffsetOf<SimEvent>(nameof(SimEvent.A)));
        Assert.Equal(20, (int)Marshal.OffsetOf<SimEvent>(nameof(SimEvent.B)));
    }

    [Fact]
    public void SizeOf_UnfixedTypes_AreStableAndMarshalConsistent()
    {
        // Размер не зафиксирован спекой, но Unsafe.SizeOf и Marshal.SizeOf обязаны совпадать (blittable).
        Assert.Equal(Unsafe.SizeOf<SimCounters>(), Marshal.SizeOf<SimCounters>());
        Assert.Equal(Unsafe.SizeOf<SimStats>(), Marshal.SizeOf<SimStats>());
        Assert.Equal(Unsafe.SizeOf<BusRouteStats>(), Marshal.SizeOf<BusRouteStats>());
        Assert.Equal(Unsafe.SizeOf<VehicleInfo>(), Marshal.SizeOf<VehicleInfo>());
        Assert.Equal(0, Unsafe.SizeOf<SimCounters>() % 8);
    }

    // ScenarioDesc и вложенные desc содержат массивы (T[]) - это вход Create, не FFI-блоб; ref struct-виды
    // (RouteView, NetworkView) не могут быть аргументом generic. Остальные публичные struct обязаны быть blittable.
    private static readonly string[] _notBlittableByDesign =
    {
        nameof(ScenarioDesc), nameof(NetworkDesc), nameof(DemandDesc), nameof(TransitDesc), nameof(BusRouteDesc), nameof(LodDesc), nameof(LodTourDesc), nameof(LoadOptions),
    };

    [Fact]
    public void AllPublicApiStructs_AreBlittable()
    {
        var types = typeof(Command).Assembly.GetExportedTypes()
            .Where(t => t.IsValueType && !t.IsEnum && !t.IsByRefLike)
            .Where(t => t.Namespace is "Sim.Core.Api" or "Sim.Core.Network" or "Sim.Core.Util")
            .Where(t => !_notBlittableByDesign.Contains(t.Name))
            .ToArray();

        Assert.NotEmpty(types);
        var check = typeof(LayoutTests).GetMethod(nameof(IsBlittable), BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var t in types)
        {
            bool blittable = (bool)check.MakeGenericMethod(t).Invoke(null, null)!;
            Assert.True(blittable, t.FullName + " содержит ссылки");
            Assert.Equal(SizeOfUnsafe(t), Marshal.SizeOf(t));
        }
    }

    private static bool IsBlittable<T>() => !RuntimeHelpers.IsReferenceOrContainsReferences<T>();

    [Fact]
    public void RefStructViews_ArePresentAndByRefLike()
    {
        Assert.True(typeof(RouteView).IsByRefLike);
        Assert.True(typeof(NetworkView).IsByRefLike);
    }
}
