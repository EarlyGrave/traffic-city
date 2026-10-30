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
}
