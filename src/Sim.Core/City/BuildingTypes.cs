using System;
using Sim.Core.Api;

namespace Sim.Core.City;

/// <summary>
/// Таблица типов зданий мира (zoning-demand.md §2.2): копия <see cref="CityDesc.BuildingTypes"/> (сейв хранит её в блоке <c>BTYP</c>, как M5 для ТС).
/// Строки только дописываются; индекс строки = <c>Building.Type</c>. Предел - <see cref="MaxTypes"/> (ushort в <c>BuildingRenderData.Type</c>; modding.md §7).
/// </summary>
internal sealed class BuildingTypes
{
    public const int MaxTypes = 512;
    public const int MaxCapacity = 2_000;

    public readonly BuildingTypeDesc[] Rows;

    public BuildingTypes(BuildingTypeDesc[]? rows)
    {
        Rows = rows == null || rows.Length == 0 ? CityDesc.BaseBuildingTypes() : (BuildingTypeDesc[])rows.Clone();
    }

    public int Count => Rows.Length;

    public ref readonly BuildingTypeDesc this[int type] => ref Rows[type];

    /// <summary>Допустимость строки: вид R/C/I/гараж/служба, плотность 0/1, вместимость в пределах, смена внутри суток (288 слотов).</summary>
    public static bool IsValid(in BuildingTypeDesc t) =>
        t.Kind is >= LotZone.Residential and <= LotZone.Service && t.Density <= 1 && t.Capacity is >= 1 and <= MaxCapacity &&
        t.ShopCapacity >= 0 && t.ShopCapacity <= 1_000_000 && t.ConstructDays is >= 0 and <= 3650 && t.AttractQ8 is >= 0 and <= 1 << 20 && t.VisualRow >= 0 &&
        t.ParkingCapacity is >= 0 and <= MaxCapacity && t.ParkingAccess <= ParkingAccess.Public && t.ParkingFee >= 0 &&
        t.DecayBpDay <= 10_000 && t.CeilDropBpDay <= 10_000 && t.DemolishCost is >= 0 and <= 100_000_000 &&
        t.BuildCost is >= 0 and <= 100_000_000 &&
        (t.Kind == LotZone.Service ? t.ServiceKind == ServiceKind.Utility && t.Vehicles is >= 1 and <= 64 : t.ServiceKind == ServiceKind.None && t.Vehicles == 0) &&
        (t.Kind is LotZone.Residential or LotZone.Parking or LotZone.Service || (t.ShiftStartSlot is >= 0 and < 288 && t.ShiftSlots is >= 1 and <= 288));

    /// <summary>Первый подходящий по виду и плотности тип (zoning-demand.md §8.3) со сдвигом <paramref name="variant"/> по подходящим; -1 - нет такого вида.</summary>
    public int Find(LotZone kind, int density, int variant)
    {
        int matches = 0;
        for (int i = 0; i < Rows.Length; i++)
        {
            if (Rows[i].Kind == kind && Rows[i].Density == density)
            {
                matches++;
            }
        }

        if (matches == 0)
        {
            return -1;
        }

        int want = variant % matches;
        for (int i = 0; i < Rows.Length; i++)
        {
            if (Rows[i].Kind == kind && Rows[i].Density == density && want-- == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
