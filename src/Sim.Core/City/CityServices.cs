using System;
using System.Runtime.InteropServices;
using Sim.Core.Api;
using Sim.Core.Util;

namespace Sim.Core.City;

/// <summary>Состояние бригады (<see cref="CrewStore.State"/>, building-lifecycle.md §2.10, services.md §2.3).</summary>
internal static class CrewState
{
    public const byte Free = 0;      // слот свободен
    public const byte Idle = 1;      // на станции, ждёт вызов
    public const byte ToSite = 2;    // едет к цели
    public const byte Working = 3;   // работает на месте до DueTick
    public const byte Return = 4;    // едет назад на станцию
}

/// <summary>
/// Вызов в кольце записи (services.md §2.3, <c>CallStore</c>): вид, запись, цель (здание + поколение), ссылка (житель или пожар, SR3/SR4; -1 - нет), тик вызова.
/// Заявка ЖКХ BL - вызов <c>repair</c> записи <c>utility</c> без ссылки.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal struct ServiceCall
{
    public int Target;
    public int TargetGen;
    public int Ref;
    public int RefGen;
    public long Tick;
    public byte Kind;   // ServiceCallKind
    public byte Svc;    // индекс записи
}

/// <summary>
/// Скомпилированные записи служб (services.md §2.2): таблицы сценария (<c>city.services</c>) либо одна встроенная запись <c>utility</c> (без секции - ЖКХ как в BL).
/// Производное по типам зданий - запись и уровень типа (вместо полей типа <c>ServiceRec/ServiceLevel</c> спеки: в сейв и хэш не входит, считается при создании).
/// </summary>
internal sealed class ServiceTable
{
    public readonly bool Explicit;                // режим city.services
    public readonly ServiceRecordDesc[] Records;
    public readonly ServiceLevelDesc[] Levels;
    public readonly ServiceUpgradeDesc[] Upgrades;
    public readonly ServiceCallDesc[] Calls;
    public readonly ServiceVisitDesc[] Visits;

    /// <summary>Запись ЖКХ (вызов <c>repair</c>, фонд <c>utility</c>) или -1.</summary>
    public readonly int Utility;

    private readonly sbyte[] _recOfType;
    private readonly byte[] _levelOfType;

    public ServiceTable(in CityDesc cfg, BuildingTypes types)
    {
        ref readonly CityServicesDesc s = ref cfg.Services;
        Explicit = s.Enabled;
        if (Explicit)
        {
            Records = s.Records ?? Array.Empty<ServiceRecordDesc>();
            Levels = s.Levels ?? Array.Empty<ServiceLevelDesc>();
            Calls = s.Calls ?? Array.Empty<ServiceCallDesc>();
        }
        else
        {
            int depot = -1;
            for (int i = 0; i < types.Count && depot < 0; i++)
            {
                depot = types.Rows[i].ServiceKind == ServiceKind.Utility ? i : -1;
            }

            short veh = depot >= 0 ? types.Rows[depot].VehicleType : CityDesc.BaseUtilityVehicle;
            CityServicesDesc.BaseRecords(depot, veh, out Records, out Levels, out Calls);
        }

        Upgrades = (Explicit ? s.Upgrades : null) ?? Array.Empty<ServiceUpgradeDesc>();
        Visits = (Explicit ? s.Visits : null) ?? Array.Empty<ServiceVisitDesc>();
        Utility = -1;
        for (int r = 0; r < Records.Length && Utility < 0; r++)
        {
            Utility = Records[r].Fund == ServiceFund.Utility && HasCall(r, ServiceCallKind.Repair) ? r : -1;
        }

        _recOfType = new sbyte[types.Count];
        _levelOfType = new byte[types.Count];
        Array.Fill(_recOfType, (sbyte)-1);
        for (int r = 0; r < Records.Length; r++)
        {
            for (int k = 0; k < Records[r].LevelCount; k++)
            {
                int t = Levels[Records[r].FirstLevel + k].Type;
                if ((uint)t < (uint)types.Count && _recOfType[t] < 0)
                {
                    _recOfType[t] = (sbyte)r;
                    _levelOfType[t] = (byte)(k + 1);
                }
            }
        }

        for (int t = 0; t < types.Count; t++)
        {
            if (_recOfType[t] < 0 && types.Rows[t].ServiceKind == ServiceKind.Utility && Utility >= 0)
            {
                _recOfType[t] = (sbyte)Utility;   // депо BL вне записей: уровень 1 записи ЖКХ
                _levelOfType[t] = 1;
            }
        }
    }

    public int Count => Records.Length;

    /// <summary>
    /// Пределы секции <c>city.services</c> и таблиц записей (services.md §2.2, §6) при <c>Create</c> и загрузке: ёмкости, диапазоны записей в границах таблиц, уровни 1..4,
    /// апгрейды 0..8, виды вызовов и поводов, <c>on_call</c> у <c>repair</c> нельзя, <c>deadline_s</c> 0 или не меньше <c>onsite_s</c>, тип уровня - индекс типа здания.
    /// </summary>
    public static bool IsValid(in CityServicesDesc s, int typeCount, int vehicleTypeCount)
    {
        bool ok = s.MaxServiceSites is >= 1 and <= 65_536 && s.MaxCalls is >= 0 and <= 1 << 20 && s.MaxFires is >= 0 and <= 65_536;
        ok &= s.DisorderIllPct is >= 0 and <= 1_000 && s.FireRiskPct is >= 0 and <= 1_000 && s.SmolderS is >= 0 and <= 86_400 && s.AnnexBuyPct is >= 0 and <= 1_000;
        ok &= s.IllLightPpm is >= 0 and <= 1_000_000 && s.IllInpatientPpm is >= 0 and <= 1_000_000 && s.IllCriticalPpm is >= 0 and <= 1_000_000;
        ok &= s.IllDaysLight is >= 1 and <= 31 && s.IllDaysInpatient is >= 1 and <= 31 && s.IllDaysCritical is >= 1 and <= 31;
        if (!ok)
        {
            return false;
        }

        ServiceRecordDesc[] rec = s.Records ?? Array.Empty<ServiceRecordDesc>();
        ServiceLevelDesc[] lv = s.Levels ?? Array.Empty<ServiceLevelDesc>();
        ServiceUpgradeDesc[] up = s.Upgrades ?? Array.Empty<ServiceUpgradeDesc>();
        ServiceCallDesc[] calls = s.Calls ?? Array.Empty<ServiceCallDesc>();
        ServiceVisitDesc[] vis = s.Visits ?? Array.Empty<ServiceVisitDesc>();
        if (rec.Length > CityServicesDesc.MaxRecords || lv.Length > CityServicesDesc.MaxRecords * CityServicesDesc.MaxLevels ||
            up.Length > CityServicesDesc.MaxRecords * CityServicesDesc.MaxLevels * CityServicesDesc.MaxUpgrades || calls.Length > CityServicesDesc.MaxRecords * 8 || vis.Length > CityServicesDesc.MaxRecords * 8)
        {
            return false;
        }

        for (int r = 0; r < rec.Length; r++)
        {
            ref readonly ServiceRecordDesc d = ref rec[r];
            ok &= d.Fund is ServiceFund.City or ServiceFund.Utility && d.LevelCount is >= 1 and <= CityServicesDesc.MaxLevels && Range(d.FirstLevel, d.LevelCount, lv.Length);
            ok &= Range(d.FirstCall, d.CallCount, calls.Length) && Range(d.FirstVisit, d.VisitCount, vis.Length);
        }

        for (int i = 0; ok && i < lv.Length; i++)
        {
            ref readonly ServiceLevelDesc l = ref lv[i];
            ok &= (uint)l.Type < (uint)typeCount && l.LotMinF >= 1 && l.LotMinD >= 1 && l.BuildCost >= 0 && l.UpkeepDay >= 0 && l.CapacityDay >= 0 && l.Beds >= 0 && l.Icu >= 0;
            ok &= l.OpenSlot is >= 0 and <= CityWorld.SlotsPerDay && l.CloseSlot is >= 0 and <= CityWorld.SlotsPerDay && l.StaySlots is >= 0 and <= CityWorld.SlotsPerDay;
            ok &= l.DispatchVehicle >= -1 && l.DispatchVehicle < vehicleTypeCount && l.Units is >= 0 and <= 64 && l.PerUnitDay >= 0;
            ok &= l.UpgradeCount is >= 0 and <= CityServicesDesc.MaxUpgrades && Range(l.FirstUpgrade, l.UpgradeCount, up.Length);
        }

        for (int i = 0; ok && i < up.Length; i++)
        {
            ok &= up[i].Cost >= 0 && up[i].UpkeepDay >= 0 && up[i].Effect is >= ServiceUpgradeEffect.Units and <= ServiceUpgradeEffect.OnsitePct && up[i].Value >= 0;
        }

        for (int i = 0; ok && i < calls.Length; i++)
        {
            ref readonly ServiceCallDesc c = ref calls[i];
            ok &= c.Kind is >= ServiceCallKind.Repair and <= ServiceCallKind.Fire && c.OnsiteS is >= 0 and <= 86_400 && c.DeadlineS >= 0 && (c.DeadlineS == 0 || c.DeadlineS >= c.OnsiteS);
            ok &= c.PerCall >= 0 && (c.Flags & ~(ServiceCallFlags.Urgent | ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn)) == 0;
            ok &= c.Kind != ServiceCallKind.Repair || (c.Flags & (ServiceCallFlags.OnCall | ServiceCallFlags.OnCallReturn)) == 0;   // ЖКХ приоритета не получает (§2.3)
        }

        for (int i = 0; ok && i < vis.Length; i++)
        {
            ok &= vis[i].Reason is ServiceVisitReason.Light or ServiceVisitReason.Inpatient && vis[i].PerVisit >= 0 && vis[i].MaxCostS >= 0;
        }

        return ok;
    }

    private static bool Range(int first, int count, int length) => first >= 0 && count >= 0 && (long)first + count <= length;

    /// <summary>Запись типа здания или -1.</summary>
    public int RecOf(int type) => (uint)type < (uint)_recOfType.Length ? _recOfType[type] : -1;

    /// <summary>Уровень (1..4) типа здания в его записи; 0 - тип не служба.</summary>
    public int LevelOf(int type) => (uint)type < (uint)_levelOfType.Length ? _levelOfType[type] : 0;

    /// <summary>У записи есть вызов вида <paramref name="kind"/>.</summary>
    public bool HasCall(int rec, ServiceCallKind kind)
    {
        ref readonly ServiceRecordDesc r = ref Records[rec];
        for (int i = 0; i < r.CallCount; i++)
        {
            if (Calls[r.FirstCall + i].Kind == kind)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>У записи есть вызов с флагом срочности <paramref name="urgent"/>.</summary>
    public bool HasCalls(int rec, bool urgent)
    {
        ref readonly ServiceRecordDesc r = ref Records[rec];
        for (int i = 0; i < r.CallCount; i++)
        {
            if (((Calls[r.FirstCall + i].Flags & ServiceCallFlags.Urgent) != 0) == urgent)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Станции служб (services.md §2.3, SoA ёмкости <c>max_service_sites</c>): здание + поколение, запись, уровень, апгрейды, машины, койки (кольца выписки по 16 суткам).
/// Слот свободен при <c>Building = -1</c>. Станция снесённого здания освобождается, только когда на неё не ссылается ни одна бригада (едущие доезжают и распускаются).
/// </summary>
internal sealed class SiteStore
{
    public const int ReleaseDays = 16;

    public readonly int Capacity;
    public readonly int[] Building;
    public readonly int[] Gen;
    public readonly byte[] Svc;
    public readonly byte[] Level;
    public readonly byte[] UpgradeMask;
    public readonly byte[] UnitsMax;
    public readonly byte[] Duty;
    public readonly int[] BedsUsed;
    public readonly int[] IcuUsed;
    public readonly int[] BedRelease;   // [site * 16 + day & 15]
    public readonly int[] IcuRelease;

    public SiteStore(int capacity)
    {
        Capacity = Math.Max(0, capacity);
        Building = new int[Capacity];
        Gen = new int[Capacity];
        Svc = new byte[Capacity];
        Level = new byte[Capacity];
        UpgradeMask = new byte[Capacity];
        UnitsMax = new byte[Capacity];
        Duty = new byte[Capacity];
        BedsUsed = new int[Capacity];
        IcuUsed = new int[Capacity];
        BedRelease = new int[Capacity * ReleaseDays];
        IcuRelease = new int[Capacity * ReleaseDays];
        Array.Fill(Building, -1);
    }

    /// <summary>Слот в исходное (свободен).</summary>
    public void Reset(int s)
    {
        Building[s] = -1;
        Gen[s] = 0;
        Svc[s] = 0;
        Level[s] = 0;
        UpgradeMask[s] = 0;
        UnitsMax[s] = 0;
        Duty[s] = 0;
        BedsUsed[s] = 0;
        IcuUsed[s] = 0;
        Array.Clear(BedRelease, s * ReleaseDays, ReleaseDays);
        Array.Clear(IcuRelease, s * ReleaseDays, ReleaseDays);
    }

    /// <summary>Станций в работе (занятых слотов).</summary>
    public int Used()
    {
        int n = 0;
        for (int s = 0; s < Capacity; s++)
        {
            n += Building[s] >= 0 ? 1 : 0;
        }

        return n;
    }
}

/// <summary>
/// Бригады служб (building-lifecycle.md §2.10 <c>CrewStore</c>, SoA ёмкости <c>max_service_vehicles</c>): станция, состояние, цель, участок цели, машина, сроки.
/// Кольцо <c>CrewArrivals</c> (фаза 8 -&gt; 9r) пусто на границе тиков, в сейв не входит. Все массивы - при создании; 0 Б/тик.
/// </summary>
internal sealed class CrewStore
{
    public readonly int Capacity;
    public readonly int[] Site;         // станция или -1
    public readonly byte[] State;       // CrewState
    public readonly int[] Target;       // здание вызова или -1
    public readonly int[] TargetGen;
    public readonly int[] SiteLot;      // участок здания вызова (обратная поездка, если здание исчезло) или -1
    public readonly int[] Vehicle;      // машина бригады (micro) или -1, пока не поставлена
    public readonly int[] VehicleGen;
    public readonly long[] DueTick;     // micro: таймаут поездки / конец работы; агрегат: прибытие / конец работы
    public readonly long[] ReqTick;     // тик вызова (задержка)
    public readonly RingBuffer<int> Arrivals;   // CrewArrivals: бригады, чьи машины прибыли в этом тике

    public CrewStore(int crews)
    {
        Capacity = Math.Max(0, crews);
        Site = new int[Capacity];
        State = new byte[Capacity];
        Target = new int[Capacity];
        TargetGen = new int[Capacity];
        SiteLot = new int[Capacity];
        Vehicle = new int[Capacity];
        VehicleGen = new int[Capacity];
        DueTick = new long[Capacity];
        ReqTick = new long[Capacity];
        Array.Fill(Site, -1);
        Array.Fill(Target, -1);
        Array.Fill(SiteLot, -1);
        Array.Fill(Vehicle, -1);
        Arrivals = new RingBuffer<int>(Math.Max(1, Capacity));
    }

    /// <summary>Бригад в работе (не свободных слотов).</summary>
    public int Crews()
    {
        int n = 0;
        for (int i = 0; i < Capacity; i++)
        {
            n += State[i] != CrewState.Free ? 1 : 0;
        }

        return n;
    }

    /// <summary>Свободных слотов бригад.</summary>
    public int FreeSlots() => Capacity - Crews();

    /// <summary>Слот в исходное (свободен).</summary>
    public void Reset(int k)
    {
        Site[k] = -1;
        State[k] = CrewState.Free;
        Target[k] = -1;
        TargetGen[k] = 0;
        SiteLot[k] = -1;
        Vehicle[k] = -1;
        VehicleGen[k] = 0;
        DueTick[k] = 0;
        ReqTick[k] = 0;
    }
}

/// <summary>
/// Службы города (services.md §2.3, §3; SR1): записи, станции, кольца вызовов (на запись два - срочные и обычные, FIFO), бригады. Есть при <c>city.lifecycle</c>
/// (ЖКХ BL - запись <c>utility</c>, заявка на ремонт - обычное кольцо этой записи); режим <c>city.services</c> лишь заменяет встроенную запись таблицами пакетов.
/// Без режима хэш пишется ровно как в BL (очередь и бригады с депо-зданием). Всё создаётся в конструкторе; 0 Б/тик.
/// </summary>
internal sealed class CityServices
{
    public readonly ServiceTable Table;
    public readonly SiteStore Sites;
    public readonly CrewStore Crews;
    public readonly RingBuffer<ServiceCall>[] Normal;   // [запись]
    public readonly RingBuffer<ServiceCall>[] Urgent;   // [запись]

    public CityServices(in CityDesc cfg, BuildingTypes types, int maxBuildings)
    {
        Table = new ServiceTable(in cfg, types);
        int crews = cfg.Lifecycle.MaxServiceVehicles;
        bool mode = Table.Explicit;

        // без режима станций не больше бригад (у депо >= 1 бригады): предел max_service_sites не меняет поведение BL
        Sites = new SiteStore(mode ? cfg.Services.MaxServiceSites : crews);
        Crews = new CrewStore(crews);
        int calls = mode ? (cfg.Services.MaxCalls > 0 ? cfg.Services.MaxCalls : Math.Max(maxBuildings / 8, 256)) : Math.Max(1, maxBuildings);
        Normal = new RingBuffer<ServiceCall>[Table.Count];
        Urgent = new RingBuffer<ServiceCall>[Table.Count];
        for (int r = 0; r < Table.Count; r++)
        {
            Normal[r] = new RingBuffer<ServiceCall>(Table.HasCalls(r, false) ? calls : 1);
            Urgent[r] = new RingBuffer<ServiceCall>(Table.HasCalls(r, true) ? calls : 1);
        }
    }

    /// <summary>Кольцо заявок ЖКХ (обычное кольцо записи <c>utility</c>); null - записи ЖКХ нет.</summary>
    public RingBuffer<ServiceCall>? UtilityQueue => Table.Utility >= 0 ? Normal[Table.Utility] : null;

    /// <summary>Здание станции бригады <paramref name="k"/> или -1.</summary>
    public int SiteBuilding(int k) => Crews.Site[k] >= 0 ? Sites.Building[Crews.Site[k]] : -1;

    /// <summary>Поколение здания станции бригады <paramref name="k"/> (0 - нет станции).</summary>
    public int SiteGen(int k) => Crews.Site[k] >= 0 ? Sites.Gen[Crews.Site[k]] : 0;

    /// <summary>
    /// Новая станция здания <paramref name="bi"/>: первый свободный слот; станции снесённых зданий без бригад перед этим освобождаются (порядок детерминирован). -1 - мест нет.
    /// </summary>
    public int AllocSite(BuildingStore b, int bi, int type)
    {
        int found = -1;
        for (int s = 0; s < Sites.Capacity; s++)
        {
            if (Sites.Building[s] >= 0 && !b.Pool.IsAlive(Sites.Building[s], Sites.Gen[s]) && !Referenced(s))
            {
                Sites.Reset(s);
            }

            if (found < 0 && Sites.Building[s] < 0)
            {
                found = s;
            }
        }

        if (found < 0)
        {
            return -1;
        }

        Sites.Reset(found);
        Sites.Building[found] = bi;
        Sites.Gen[found] = b.Pool.GenerationOf(bi);
        Sites.Svc[found] = (byte)Math.Max(0, Table.RecOf(type));
        Sites.Level[found] = (byte)Table.LevelOf(type);
        return found;
    }

    /// <summary>Есть ли свободная станция (с учётом освобождаемых при <see cref="AllocSite"/>).</summary>
    public bool CanAllocSite(BuildingStore b)
    {
        for (int s = 0; s < Sites.Capacity; s++)
        {
            if (Sites.Building[s] < 0 || (!b.Pool.IsAlive(Sites.Building[s], Sites.Gen[s]) && !Referenced(s)))
            {
                return true;
            }
        }

        return false;
    }

    private bool Referenced(int s)
    {
        for (int k = 0; k < Crews.Capacity; k++)
        {
            if (Crews.State[k] != CrewState.Free && Crews.Site[k] == s)
            {
                return true;
            }
        }

        return false;
    }
}
