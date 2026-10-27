using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Util;

namespace Sim.Core.Parking;

/// <summary>Вид объекта стоянки (<see cref="ParkingStore.Kind"/>; числа лежат в сейве).</summary>
internal static class ParkKind
{
    public const byte Overflow = 0;   // только слот 0: машина жителя без места, привязана к участку (CarLot)
    public const byte Curb = 1;       // обочина участка
    public const byte Building = 2;   // стоянка здания
    public const byte Garage = 3;     // гараж (здание вида Parking)
}

/// <summary>Биты <see cref="ParkingStore.Flags"/>.</summary>
internal static class ParkFlags
{
    public const byte Public = 1;     // гости любых зданий
    public const byte Permit = 2;     // можно закрепить как домашнее место
    public const byte Closing = 4;    // без новых резервов и закреплений
}

/// <summary>
/// Объекты стоянки (parking.md §2.1, ADR-0026): SoA с ёмкостью <c>max_parking</c> и <see cref="HandlePool"/> (index + generation, LIFO free-list). Слот 0 - <c>Overflow</c>, создаётся
/// в конструкторе и не освобождается. Состояние (блок <c>PARK</c>): вид, флаги, участок, здание, ёмкость, три счётчика, плата. Производные (не сохраняются и не хэшируются,
/// <see cref="ParkingBuild.RefreshAll"/>): положение, точка въезда машины, точка пешехода, компоненты связности, ходьба до двери, окрестности участков (<see cref="Catch"/>) и
/// обратные ссылки участок -> обочина, здание -> стоянка. Мёртвые слоты в сейве - канон (<see cref="Lot"/> и <see cref="Building"/> = -1, остальное 0).
/// </summary>
internal sealed class ParkingStore
{
    public readonly ParkingDesc Cfg;
    public readonly HandlePool Pool;

    // ---- состояние ----
    public readonly byte[] Kind;
    public readonly byte[] Flags;
    public readonly int[] Lot;           // участок-якорь (обочина: участок фасада; здание и гараж: участок здания); Overflow: -1
    public readonly int[] Building;      // здание-владелец (Building, Garage) или -1
    public readonly int[] BuildingGen;
    public readonly int[] Capacity;
    public readonly int[] Assigned;      // закреплено домашних мест
    public readonly int[] Occupied;      // стоят гости (Overflow: стоят машины без места)
    public readonly int[] Reserved;      // резервы машин и заявок в пути
    public readonly int[] Fee;           // плата за постановку гостя

    // ---- производные ----
    public readonly float[] X;
    public readonly float[] Z;
    public readonly int[] AccessLane;    // CarLane участка (LaneNo 0) или -1
    public readonly float[] AccessS;     // м вдоль AccessLane: CarS участка в [0, Length - MinGap]
    public readonly int[] PedEdge;
    public readonly int[] PedSMm;
    public readonly int[] RoadComp;
    public readonly int[] PedComp;
    public readonly int[] WalkMm;        // от точки пешехода объекта до центра своего участка
    public readonly int[] HomeAway;      // закрепивших, чья машина стоит не здесь

    public readonly int[] CurbOfLot;     // [max_lots] объект-обочина участка или -1
    public readonly int[] FacOfBuilding; // [max_buildings] стоянка здания или -1

    public readonly Catchment Catch;

    /// <summary>Версия для клиента (<c>ParkingVersion</c>): растёт при создании, сносе и смене ёмкости.</summary>
    public int Version;

    public ParkingStore(in ParkingDesc cfg, in SimCapacities caps, in CityGrid grid)
    {
        Cfg = cfg;
        int n = CapacityFor(in caps);
        Pool = new HandlePool(n);
        Kind = new byte[n];
        Flags = new byte[n];
        Lot = new int[n];
        Building = new int[n];
        BuildingGen = new int[n];
        Capacity = new int[n];
        Assigned = new int[n];
        Occupied = new int[n];
        Reserved = new int[n];
        Fee = new int[n];
        X = new float[n];
        Z = new float[n];
        AccessLane = new int[n];
        AccessS = new float[n];
        PedEdge = new int[n];
        PedSMm = new int[n];
        RoadComp = new int[n];
        PedComp = new int[n];
        WalkMm = new int[n];
        HomeAway = new int[n];
        CurbOfLot = new int[Math.Max(1, caps.MaxLots)];
        FacOfBuilding = new int[Math.Max(1, caps.MaxBuildings)];
        Array.Fill(CurbOfLot, -1);
        Array.Fill(FacOfBuilding, -1);
        for (int i = 0; i < n; i++)
        {
            ResetSlot(i);
        }

        Catch = new Catchment(this, in grid, Math.Max(1, caps.MaxLots), cfg.MaxCandidates, n);

        // Слот 0: Overflow (parking.md §2.1). Неограниченная ёмкость; в окрестности не входит.
        Pool.TryAllocate(out int overflow, out _);
        Kind[overflow] = ParkKind.Overflow;
        Capacity[overflow] = int.MaxValue;
        Version++;
    }

    /// <summary>Ёмкость пула: <c>capacities.max_parking</c> либо при 0 - <c>max_lots + max_buildings + 64</c> (parking.md §14).</summary>
    public static int CapacityFor(in SimCapacities caps) =>
        caps.MaxParking > 0 ? caps.MaxParking : checked(caps.MaxLots + caps.MaxBuildings + 64);

    public int HighWater => Pool.HighWater;

    public bool IsAlive(int f) => Pool.IsAlive(f);

    public bool IsAlive(int f, int gen) => Pool.IsAlive(f, gen);

    /// <summary>Свободных мест для решений (parking.md §2.1): ноль, если ёмкость уменьшили ниже занятого.</summary>
    public int Free(int f)
    {
        long free = (long)Capacity[f] - Assigned[f] - Occupied[f] - Reserved[f];
        return free <= 0 ? 0 : (free > int.MaxValue ? int.MaxValue : (int)free);
    }

    /// <summary>Создаёт объект. false - ёмкость <c>max_parking</c> исчерпана.</summary>
    public bool TryCreate(byte kind, byte flags, int lot, int building, int buildingGen, int capacity, int fee, out int f)
    {
        if (!Pool.TryAllocate(out f, out _))
        {
            f = -1;
            return false;
        }

        ResetSlot(f);
        Kind[f] = kind;
        Flags[f] = flags;
        Lot[f] = lot;
        Building[f] = building;
        BuildingGen[f] = buildingGen;
        Capacity[f] = capacity;
        Fee[f] = fee;
        if (kind == ParkKind.Curb)
        {
            CurbOfLot[lot] = f;
        }
        else if (building >= 0)
        {
            FacOfBuilding[building] = f;
        }

        Version++;
        return true;
    }

    /// <summary>Освобождает слот (счётчики уже обнулены вызывающим: жители переназначены).</summary>
    public void Destroy(int f)
    {
        if (Kind[f] == ParkKind.Curb)
        {
            if (Lot[f] >= 0)
            {
                CurbOfLot[Lot[f]] = -1;
            }
        }
        else if (Building[f] >= 0)
        {
            FacOfBuilding[Building[f]] = -1;
        }

        ResetSlot(f);
        Pool.Free(f);
        Version++;
    }

    /// <summary>Загрузка: обратные ссылки участок -> обочина, здание -> стоянка из состояния.</summary>
    public void RebuildLookups()
    {
        Array.Fill(CurbOfLot, -1);
        Array.Fill(FacOfBuilding, -1);
        for (int f = 1; f < Pool.HighWater; f++)
        {
            if (!Pool.IsAlive(f))
            {
                continue;
            }

            if (Kind[f] == ParkKind.Curb)
            {
                if ((uint)Lot[f] < (uint)CurbOfLot.Length)
                {
                    CurbOfLot[Lot[f]] = f;
                }
            }
            else if ((uint)Building[f] < (uint)FacOfBuilding.Length)
            {
                FacOfBuilding[Building[f]] = f;
            }
        }
    }

    /// <summary>Канонический слот: ссылки -1, остальное 0 (то, что пишет сейв для мёртвых слотов).</summary>
    private void ResetSlot(int i)
    {
        Kind[i] = 0;
        Flags[i] = 0;
        Lot[i] = -1;
        Building[i] = -1;
        BuildingGen[i] = 0;
        Capacity[i] = 0;
        Assigned[i] = 0;
        Occupied[i] = 0;
        Reserved[i] = 0;
        Fee[i] = 0;
        X[i] = 0f;
        Z[i] = 0f;
        AccessLane[i] = -1;
        AccessS[i] = 0f;
        PedEdge[i] = -1;
        PedSMm[i] = 0;
        RoadComp[i] = -1;
        PedComp[i] = -1;
        WalkMm[i] = 0;
        HomeAway[i] = 0;
    }
}
