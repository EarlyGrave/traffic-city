using System;
using System.Runtime.InteropServices;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Save;
using Sim.Core.Tests.Save;

namespace Sim.Core.Tests.Worlds;

/// <summary>
/// Классы ТС, задача VC1 (vehicle-classes.md §7, тесты 1 и 7; ADR-0039): категории 0-4, <c>World.TypeAccess</c>, флаг <c>OnCall</c> в валидаторе сейва.
/// Поведения (маски полос и улиц, приоритет на вызове) здесь нет - это VC2 и VC3.
/// </summary>
public class VehicleClassesTests
{
    private static ScenarioDesc WithAllClasses()
    {
        ScenarioDesc d = WorldSaveFixtures.Small();
        VehicleTypeDesc car = d.VehicleTypes[0];
        var types = new VehicleTypeDesc[5];
        for (int i = 0; i < types.Length; i++)
        {
            types[i] = car;
        }

        types[1].Category = VehicleCategory.Bus;
        types[2].Category = VehicleCategory.Van;
        types[3].Category = VehicleCategory.Truck;
        types[4].Category = VehicleCategory.Special;
        d.VehicleTypes = types;
        d.Demand.TypeMix = new[] { 1f, 0f, 0f, 0f, 0f };   // спавн прежний: только легковые
        d.Capacities.MaxVehicles = 128;
        return d;
    }

    [Fact]
    public void CategoryNumbers_AreFrozen()
    {
        Assert.Equal(0, (int)VehicleCategory.Car);
        Assert.Equal(1, (int)VehicleCategory.Bus);
        Assert.Equal(2, (int)VehicleCategory.Van);
        Assert.Equal(3, (int)VehicleCategory.Truck);
        Assert.Equal(4, (int)VehicleCategory.Special);
        Assert.Equal(128, (int)VehicleFlags.OnCall);
        Assert.Equal(0x20, VehicleAccess.Emergency);
    }

    [Fact]
    public void TypeAccess_IsClassBitPerType()
    {
        ScenarioDesc d = WithAllClasses();
        using Simulation sim = Simulation.Create(in d);
        var want = new byte[256];   // VC2: таблица на все 256 номеров типа (индекс - byte Type, без проверки границ); незаданные - 0
        new byte[] { VehicleAccess.Car, VehicleAccess.Bus, VehicleAccess.Van, VehicleAccess.Truck, VehicleAccess.Special }.CopyTo(want, 0);
        Assert.Equal(want, sim.World.TypeAccess);
    }

    [Fact]
    public void Create_RejectsUnknownCategory()
    {
        ScenarioDesc d = WithAllClasses();
        d.VehicleTypes[2].Category = (VehicleCategory)5;
        Assert.Equal(CreateStatus.InvalidVehicleType, Simulation.TryCreate(in d, out Simulation? sim));
        Assert.Null(sim);
    }

    private static byte[] SaveBytes()
    {
        ScenarioDesc d = WithAllClasses();
        using Simulation sim = Simulation.Create(in d);
        sim.Step(700);
        return WorldSaveFixtures.SaveBytes(sim);
    }

    /// <summary>Правит тип и флаги первой машины и записывает файл заново с верными контрольными суммами.</summary>
    private static byte[] PatchFirstVehicle(byte[] file, byte type, byte flags)
    {
        Assert.Equal(LoadStatus.Ok, SaveReader.Read(file, null, out SaveTables? t, out _));
        int b = t!.FindBlock(SaveFormat.FourCc("VEHC"u8));
        Assert.True(b >= 0);
        Assert.True(t.TryGetColumnBytes(b, 10, out ReadOnlySpan<byte> types));
        Assert.True(t.TryGetColumnBytes(b, 35, out ReadOnlySpan<byte> fl));
        Assert.True(types.Length > 0);
        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(types), types.Length)[0] = type;
        MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(fl), fl.Length)[0] = flags;
        SaveHeaderInfo h = t.Header;
        Assert.Equal(SaveStatus.Ok, SaveWriter.Measure(ref t, out long len));
        byte[] buf = new byte[len];
        Assert.Equal(SaveStatus.Ok, SaveWriter.Write(ref t, in h, buf, out _));
        return buf;
    }

    [Fact]
    public void Save_OnCallFlag_OnlyOnSpecialType()
    {
        byte[] save = SaveBytes();
        Assert.True(Simulation.CurrentWorldVersion >= 17);

        // Контроль: спецмашина на вызове грузится.
        byte[] ok = PatchFirstVehicle(save, 4, (byte)VehicleFlags.OnCall);
        Assert.Equal(LoadStatus.Ok, Simulation.Load(ok, default, default, out Simulation? s, out _));
        s!.Dispose();

        // Флаг у легковой, автобуса, фургона и фуры - ошибка валидатора, не исключение.
        foreach (byte type in new byte[] { 0, 1, 2, 3 })
        {
            byte[] bad = PatchFirstVehicle(save, type, (byte)VehicleFlags.OnCall);
            LoadStatus st = LoadStatus.Ok;
            Exception? ex = Record.Exception(() => st = Simulation.Load(bad, default, default, out _, out _));
            Assert.Null(ex);
            Assert.Equal(LoadStatus.InvalidValue, st);
        }
    }

    [Fact]
    public void Save_RoundTrip_KeepsAllCategories()
    {
        byte[] save = SaveBytes();
        Assert.Equal(LoadStatus.Ok, Simulation.Load(save, default, default, out Simulation? s, out _));
        using (s)
        {
            Assert.Equal(VehicleCategory.Truck, s!.World.Types[3].Category);
            Assert.Equal(VehicleCategory.Special, s.World.Types[4].Category);
            Assert.Equal(VehicleAccess.Special, s.World.TypeAccess[4]);
        }
    }
}
