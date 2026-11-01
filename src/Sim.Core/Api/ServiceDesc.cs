using System;

namespace Sim.Core.Api;

// Записи служб (services.md §2.2, ADR-0041): служба = данные, поведение - перечисления кода. Таблицы плоские (FFI, сейв как BTYP): запись ссылается на свои уровни,
// вызовы и поводы визита диапазонами [First, First + Count) общих массивов, уровень - на свои апгрейды. Сроки - в секундах симуляции (_s), деньги - целые.

/// <summary>Счёт оплаты службы (§2.2 <c>fund</c>).</summary>
public enum ServiceFund : byte
{
    City = 0,
    Utility = 1,
}

/// <summary>Роли уровня (§2.2 <c>role[]</c>), битовая маска.</summary>
[Flags]
public enum ServiceRole : byte
{
    None = 0,
    Repair = 1,
    Light = 2,
    Inpatient = 4,
    Critical = 8,
    Fire = 16,
}

/// <summary>Вид выездного вызова (§2.2 <c>calls[].kind</c>).</summary>
public enum ServiceCallKind : byte
{
    None = 0,
    Repair = 1,
    Critical = 2,
    Fire = 3,
}

/// <summary>Повод визита «к ним ходят» (§2.2 <c>visits[].reason</c>).</summary>
public enum ServiceVisitReason : byte
{
    None = 0,
    Light = 1,
    Inpatient = 2,
}

/// <summary>Эффект апгрейда (§2.2 <c>upgrades[].effect</c>); эффекта «в обход сети» нет и не будет без решения владельца.</summary>
public enum ServiceUpgradeEffect : byte
{
    None = 0,
    Units = 1,
    VisitCapacity = 2,
    Beds = 3,
    Icu = 4,
    OnsitePct = 5,
}

/// <summary>Флаги вызова (<see cref="ServiceCallDesc.Flags"/>).</summary>
public static class ServiceCallFlags
{
    public const byte Urgent = 1;
    public const byte OnCall = 2;
    public const byte OnCallReturn = 4;
}

/// <summary>Запись службы (§2.2): счёт и диапазоны уровней, вызовов и поводов визита в таблицах <see cref="CityServicesDesc"/>.</summary>
public struct ServiceRecordDesc
{
    public ServiceFund Fund;
    public ushort Rev;
    public int FirstLevel;
    public int LevelCount;          // 1..4
    public int FirstCall;
    public int CallCount;
    public int FirstVisit;
    public int VisitCount;
}

/// <summary>Уровень записи (§2.2 <c>levels[]</c>): тип здания, роли, участок, цены, визиты, выезд, апгрейды.</summary>
public struct ServiceLevelDesc
{
    public int Type;                // индекс типа здания (вид Service)
    public ServiceRole Roles;
    public byte LotMinF;            // lot_min: фасад, клеток
    public byte LotMinD;            // lot_min: глубина, клеток
    public byte Milestone;          // индекс вехи (progression.md §3), 0 - сразу
    public int BuildCost;
    public int UpkeepDay;
    public int CapacityDay;         // visit.capacity_day; 0 - визитов нет
    public int Beds;                // visit.beds
    public int Icu;                 // visit.icu
    public short OpenSlot;          // visit.open_slot, слот суток 0..288
    public short CloseSlot;         // visit.close_slot
    public short StaySlots;         // visit.stay_slots
    public short DispatchVehicle;   // dispatch.vehicle: индекс типа ТС; -1 - выезда нет
    public int Units;               // dispatch.units
    public int PerUnitDay;          // dispatch.per_unit_day
    public int FirstUpgrade;
    public int UpgradeCount;        // 0..8
}

/// <summary>Локальный апгрейд уровня (§2.2 <c>upgrades[]</c>).</summary>
public struct ServiceUpgradeDesc
{
    public int Cost;
    public int UpkeepDay;
    public byte Milestone;
    public ServiceUpgradeEffect Effect;
    public int Value;
}

/// <summary>Выездной вызов записи (§2.2 <c>calls[]</c>).</summary>
public struct ServiceCallDesc
{
    public ServiceCallKind Kind;
    public byte Flags;              // ServiceCallFlags
    public int OnsiteS;
    public int DeadlineS;           // 0 - без срока
    public int PerCall;
}

/// <summary>Повод визита записи (§2.2 <c>visits[]</c>).</summary>
public struct ServiceVisitDesc
{
    public ServiceVisitReason Reason;
    public ServiceRole MinRole;
    public int PerVisit;
    public int MaxCostS;
}

/// <summary>
/// Секция <c>city.services</c> (services.md §2.1, §6) и таблицы записей служб из пакетов. <c>Enabled = false</c> - секции нет: ЖКХ работает ровно как в BL (встроенная запись <c>utility</c>,
/// таблицы пусты). Требует <c>city.lifecycle</c>.
/// </summary>
public struct CityServicesDesc
{
    /// <summary>Предел записей в наборе (<c>ModLimits.MaxServices</c>).</summary>
    public const int MaxRecords = 32;

    /// <summary>Уровней в записи.</summary>
    public const int MaxLevels = 4;

    /// <summary>Апгрейдов на уровень (бит <c>UpgradeMask</c>).</summary>
    public const int MaxUpgrades = 8;

    public bool Enabled;
    public int MaxServiceSites;     // max_service_sites: ёмкость SiteStore; дефолт 256
    public int MaxCalls;            // max_calls: ёмкость кольца вызовов записи; 0 - max(max_buildings / 8, 256)
    public int MaxFires;            // max_fires: пул пожаров (SR4); дефолт 256
    public int DisorderIllPct;      // disorder_ill_pct (SR3); дефолт 125
    public int FireRiskPct;         // fire_risk_pct (SR4); дефолт 10
    public int SmolderS;            // smolder_s (SR4); дефолт 120
    public int FireCap;             // fire_cap (SR4, решение 10.1 Б): потолок одновременных очагов (ожидающие + горящие), не больше max_fires; дефолт 32
    public int FireSpreadPpm;       // fire_spread_ppm (SR4): шанс за минуту огня перекинуться на дом той же клетки 128 м, ppm; дефолт 30 000 (3 %)
    public int AnnexBuyPct;         // annex_buy_pct (SR5); дефолт 150
    public int IllLightPpm;         // incidence_ppm.adult[0] (SR3); дефолт 3 000
    public int IllInpatientPpm;     // incidence_ppm.adult[1]; дефолт 600
    public int IllCriticalPpm;      // incidence_ppm.adult[2]; дефолт 300
    public int IllDaysLight;        // ill_days[0]; дефолт 1
    public int IllDaysInpatient;    // ill_days[1]; дефолт 3
    public int IllDaysCritical;     // ill_days[2]; дефолт 5

    public ServiceRecordDesc[] Records;
    public ServiceLevelDesc[] Levels;
    public ServiceUpgradeDesc[] Upgrades;
    public ServiceCallDesc[] Calls;
    public ServiceVisitDesc[] Visits;

    /// <summary>Дефолты §6 с включённым режимом и пустыми таблицами (таблицы заполняет загрузчик).</summary>
    public static CityServicesDesc Default() => new()
    {
        Enabled = true,
        MaxServiceSites = 256,
        MaxFires = 256,
        DisorderIllPct = 125,
        FireRiskPct = 10,
        SmolderS = 120,
        FireCap = 32,
        FireSpreadPpm = 30_000,
        AnnexBuyPct = 150,
        IllLightPpm = 3_000,
        IllInpatientPpm = 600,
        IllCriticalPpm = 300,
        IllDaysLight = 1,
        IllDaysInpatient = 3,
        IllDaysCritical = 5,
        Records = Array.Empty<ServiceRecordDesc>(),
        Levels = Array.Empty<ServiceLevelDesc>(),
        Upgrades = Array.Empty<ServiceUpgradeDesc>(),
        Calls = Array.Empty<ServiceCallDesc>(),
        Visits = Array.Empty<ServiceVisitDesc>(),
    };

    /// <summary>
    /// Встроенная запись <c>utility</c> (= <c>content/base/services.json</c>, тест сверяет): фонд ЖКХ, вызов <c>repair</c> без приоритета, один уровень - депо <paramref name="depotType"/>
    /// с машинами <paramref name="vehicle"/>. Число бригад в SR1 по-прежнему задаёт тип здания (<c>Vehicles</c>), <c>units</c> записи - справочно до SR2.
    /// </summary>
    public static void BaseRecords(int depotType, short vehicle, out ServiceRecordDesc[] records, out ServiceLevelDesc[] levels, out ServiceCallDesc[] calls)
    {
        records = new[] { new ServiceRecordDesc { Fund = ServiceFund.Utility, Rev = 1, FirstLevel = 0, LevelCount = 1, FirstCall = 0, CallCount = 1 } };
        levels = new[]
        {
            new ServiceLevelDesc
            {
                Type = depotType, Roles = ServiceRole.Repair, LotMinF = 1, LotMinD = 1, BuildCost = 20_000, DispatchVehicle = vehicle, Units = 16, PerUnitDay = 60,
            },
        };
        calls = new[] { new ServiceCallDesc { Kind = ServiceCallKind.Repair, OnsiteS = 150 } };
    }

    /// <summary>
    /// Встроенный набор записей <c>utility</c>, <c>medicine</c>, <c>fire</c> (SR6; = <c>content/base/services.json</c>, тест сверяет) для сценариев v2 с <c>city.services</c>:
    /// типы зданий - <see cref="CityDesc.BaseServiceBuildingTypes"/>, дописанные в таблицу с индекса <paramref name="firstServiceType"/> (медпункт, больница, медцентр, пожарная часть, пожарное управление).
    /// </summary>
    public static void BaseAllRecords(int depotType, short utilityVehicle, int firstServiceType, out ServiceRecordDesc[] records, out ServiceLevelDesc[] levels, out ServiceUpgradeDesc[] upgrades, out ServiceCallDesc[] calls, out ServiceVisitDesc[] visits)
    {
        BaseRecords(depotType, utilityVehicle, out ServiceRecordDesc[] ur, out ServiceLevelDesc[] ul, out ServiceCallDesc[] uc);
        int t = firstServiceType;
        records = new[]
        {
            ur[0],
            new ServiceRecordDesc { Fund = ServiceFund.City, Rev = 1, FirstLevel = 1, LevelCount = 3, FirstCall = 1, CallCount = 1, FirstVisit = 0, VisitCount = 2 },
            new ServiceRecordDesc { Fund = ServiceFund.City, Rev = 1, FirstLevel = 4, LevelCount = 2, FirstCall = 2, CallCount = 1, FirstVisit = 2, VisitCount = 0 },
        };
        levels = new[]
        {
            ul[0],
            new ServiceLevelDesc
            {
                Type = t, Roles = ServiceRole.Light, LotMinF = 2, LotMinD = 2, Milestone = 0, BuildCost = 15_000, UpkeepDay = 250, CapacityDay = 60, OpenSlot = 96, CloseSlot = 216, StaySlots = 6,
                DispatchVehicle = -1, FirstUpgrade = 0, UpgradeCount = 1,
            },
            new ServiceLevelDesc
            {
                Type = t + 1, Roles = ServiceRole.Light | ServiceRole.Inpatient, LotMinF = 4, LotMinD = 4, Milestone = 2, BuildCost = 60_000, UpkeepDay = 900, CapacityDay = 150, Beds = 20,
                OpenSlot = 84, CloseSlot = 240, StaySlots = 12, DispatchVehicle = -1, FirstUpgrade = 1, UpgradeCount = 1,
            },
            new ServiceLevelDesc
            {
                Type = t + 2, Roles = ServiceRole.Light | ServiceRole.Inpatient | ServiceRole.Critical, LotMinF = 6, LotMinD = 5, Milestone = 3, BuildCost = 180_000, UpkeepDay = 2_400, CapacityDay = 300,
                Beds = 60, Icu = 15, OpenSlot = 0, CloseSlot = 288, StaySlots = 12, DispatchVehicle = CityDesc.BaseAmbulanceVehicle, Units = 4, PerUnitDay = 80, FirstUpgrade = 2, UpgradeCount = 2,
            },
            new ServiceLevelDesc
            {
                Type = t + 3, Roles = ServiceRole.Fire, LotMinF = 3, LotMinD = 3, Milestone = 0, BuildCost = 25_000, UpkeepDay = 300, DispatchVehicle = CityDesc.BaseFireVehicle, Units = 2,
                PerUnitDay = 60, FirstUpgrade = 4, UpgradeCount = 0,
            },
            new ServiceLevelDesc
            {
                Type = t + 4, Roles = ServiceRole.Fire, LotMinF = 5, LotMinD = 4, Milestone = 3, BuildCost = 80_000, UpkeepDay = 900, DispatchVehicle = CityDesc.BaseFireVehicle, Units = 5,
                PerUnitDay = 60, FirstUpgrade = 4, UpgradeCount = 1,
            },
        };
        upgrades = new[]
        {
            new ServiceUpgradeDesc { Cost = 5_000, UpkeepDay = 60, Effect = ServiceUpgradeEffect.VisitCapacity, Value = 30 },
            new ServiceUpgradeDesc { Cost = 25_000, UpkeepDay = 250, Effect = ServiceUpgradeEffect.Beds, Value = 15 },
            new ServiceUpgradeDesc { Cost = 30_000, UpkeepDay = 160, Effect = ServiceUpgradeEffect.Units, Value = 2 },
            new ServiceUpgradeDesc { Cost = 50_000, UpkeepDay = 400, Effect = ServiceUpgradeEffect.Icu, Value = 10 },
            new ServiceUpgradeDesc { Cost = 20_000, UpkeepDay = 150, Effect = ServiceUpgradeEffect.OnsitePct, Value = 75 },
        };
        calls = new[]
        {
            uc[0],
            new ServiceCallDesc { Kind = ServiceCallKind.Critical, Flags = ServiceCallFlags.Urgent | ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn, OnsiteS = 120, DeadlineS = 600, PerCall = 150 },
            new ServiceCallDesc { Kind = ServiceCallKind.Fire, Flags = ServiceCallFlags.Urgent | ServiceCallFlags.OnCall, OnsiteS = 60, PerCall = 200 },
        };
        visits = new[]
        {
            new ServiceVisitDesc { Reason = ServiceVisitReason.Light, MinRole = ServiceRole.Light, PerVisit = 10, MaxCostS = 1_200 },
            new ServiceVisitDesc { Reason = ServiceVisitReason.Inpatient, MinRole = ServiceRole.Inpatient, PerVisit = 40, MaxCostS = 1_800 },
        };
    }
}
