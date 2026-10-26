using System;
using Sim.Core.Api;
using Sim.Core.City;
using Sim.Core.Demand;
using Sim.Core.Util;

namespace Sim.Core.Save;

// Блоки и столбцы города (world_version 3, zoning-demand.md §15): BTYP (PreWorld), LOTS, BLDG, RESD, SKIM, LEDG - есть ⇔ demand.model = residents;
// столбцы PARM 113-184, WRLD 14-18, CNTR 118-149, VEHC 41. Поверхность заморожена в Z1: логика меняется, схема - нет (добавления столбцов - только через MigrationV3 до закрытия w3).
internal static partial class SaveSchema
{
    private static readonly Func<World, HandlePool> _bldPool = static w => w.City!.Buildings.Pool;
    private static readonly Func<World, int> _bldHw = static w => w.City!.Buildings.Pool.HighWater;
    private static readonly Func<World, HandlePool> _resdPool = static w => w.City!.Residents.Pool;
    private static readonly Func<World, int> _resdHw = static w => w.City!.Residents.Pool.HighWater;
    private static readonly Func<World, int> _resdPlanN = static w => w.City!.Residents.Pool.HighWater * CityResidents.MaxTours;
    private static readonly Func<World, int> _bldPairN = static w => 2 * w.City!.Buildings.Pool.HighWater;
    private static readonly Func<World, int> _lotN = static w => w.City!.Lots.Count;

    private static Func<World, bool> CityPresent => static w => w.City != null;

    // ---- BTYP: таблица типов зданий (PreWorld, как VTYP) ----

    private static BlockDef BuildingTypesBlock() => new("BTYP", "типы зданий: симуляционные параметры (zoning-demand.md §2.2, §17); строки только дописываются", new[]
    {
        PreField<BuildingTypeDesc, byte>(1, "Kind", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => (byte)t.Kind, static (ref BuildingTypeDesc t, byte v) => t.Kind = (LotZone)v),
        PreField<BuildingTypeDesc, byte>(2, "Density", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.Density, static (ref BuildingTypeDesc t, byte v) => t.Density = v),
        PreField<BuildingTypeDesc, int>(3, "Capacity", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.Capacity, static (ref BuildingTypeDesc t, int v) => t.Capacity = v),
        PreField<BuildingTypeDesc, int>(4, "ShopCapacity", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.ShopCapacity, static (ref BuildingTypeDesc t, int v) => t.ShopCapacity = v),
        PreField<BuildingTypeDesc, int>(5, "ShiftStartSlot", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.ShiftStartSlot, static (ref BuildingTypeDesc t, int v) => t.ShiftStartSlot = v),
        PreField<BuildingTypeDesc, int>(6, "ShiftSlots", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.ShiftSlots, static (ref BuildingTypeDesc t, int v) => t.ShiftSlots = v),
        PreField<BuildingTypeDesc, int>(7, "ConstructDays", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.ConstructDays, static (ref BuildingTypeDesc t, int v) => t.ConstructDays = v),
        PreField<BuildingTypeDesc, int>(8, "AttractQ8", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.AttractQ8, static (ref BuildingTypeDesc t, int v) => t.AttractQ8 = v),
        PreField<BuildingTypeDesc, int>(9, "VisualRow", 5, "Rows.Length", w => w.City?.Types.Rows, c => c.BuildingTypes, (c, a) => c.BuildingTypes = a, static (in BuildingTypeDesc t) => t.VisualRow, static (ref BuildingTypeDesc t, int v) => t.VisualRow = v),
    })
    {
        PreWorld = true,
        PresentText = "demand.model = residents",
        Present = CityPresent,
    };

    // ---- LOTS: участки (состояние; производное - геометрия, ячейка, точки доступа, компоненты) ----

    private static BlockDef LotsBlock() => new("LOTS", "участки: сегмент, сторона, отрезок фасада, зона, плотность, здание, флаги (zoning-demand.md §2.1)", new[]
    {
        Col.Scalar<int>(1, "Count", w => w.City!.Lots.Count, (c, v) =>
        {
            if (v < 0 || v > c.W.City!.Lots.Capacity)
            {
                return LoadStatus.CapacityExceeded;
            }

            c.W.City.Lots.Count = v;
            return LoadStatus.Ok;
        }),
        Col.Array<int>(2, "Seg", w => w.City!.Lots.Seg, _lotN, "Count"),
        Col.Array<int>(3, "SegGen", w => w.City!.Lots.SegGen, _lotN, "Count"),
        Col.Array<byte>(4, "Side", w => w.City!.Lots.Side, _lotN, "Count"),
        Col.Array<int>(5, "S0Mm", w => w.City!.Lots.S0Mm, _lotN, "Count"),
        Col.Array<int>(6, "S1Mm", w => w.City!.Lots.S1Mm, _lotN, "Count"),
        Col.Array<byte>(7, "Zone", w => w.City!.Lots.Zone, _lotN, "Count"),
        Col.Array<byte>(8, "Density", w => w.City!.Lots.Density, _lotN, "Count"),
        Col.Array<int>(9, "Building", w => w.City!.Lots.Building, _lotN, "Count", "-1"),
        Col.Array<byte>(10, "Flags", w => w.City!.Lots.Flags, _lotN, "Count"),
    })
    {
        PresentText = "demand.model = residents",
        Present = CityPresent,
    };

    // ---- BLDG: здания (пул с поколениями, free-list в LIFO-порядке; мёртвые слоты - канон) ----

    private static BlockDef BuildingsBlock() => new("BLDG", "здания: пул (поколения, free-list) и поля; мёртвые слоты - канон (ссылки -1, остальное 0)", new[]
    {
        PoolHighWater(1, _bldPool),
        PoolFreeCount(2, _bldPool),
        Col.Array<int>(3, "FreeList", w => w.City!.Buildings.Pool.FreeListArray, w => w.City!.Buildings.Pool.FreeCount, "FreeCount"),
        Col.Array<int>(4, "Generation", w => w.City!.Buildings.Pool.GenerationArray, _bldHw, "HighWater"),
        Col.Array<int>(5, "Lot", w => w.City!.Buildings.Lot, _bldHw, "HighWater", "-1", _bldPool, -1),
        Col.Array<int>(6, "Type", w => w.City!.Buildings.Type, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<byte>(7, "State", w => w.City!.Buildings.State, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<long>(8, "StateTick", w => w.City!.Buildings.StateTick, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<int>(9, "Occupants", w => w.City!.Buildings.Occupants, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<int>(10, "Jobs", w => w.City!.Buildings.Jobs, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<int>(11, "HeadRes", w => w.City!.Buildings.HeadRes, _bldHw, "HighWater", "-1", _bldPool, -1),
        Col.Array<int>(12, "HeadWork", w => w.City!.Buildings.HeadWork, _bldHw, "HighWater", "-1", _bldPool, -1),
        Col.Array<long>(13, "Account", w => w.City!.Buildings.Account, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<long>(14, "RevenueEwma", w => w.City!.Buildings.RevenueEwma, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<long>(15, "ProfitEwma", w => w.City!.Buildings.ProfitEwma, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<int>(16, "VisitsDay", w => w.City!.Buildings.VisitsDay, _bldPairN, "2 * HighWater"),
        Col.Array<long>(17, "RevenueDay", w => w.City!.Buildings.RevenueDay, _bldPairN, "2 * HighWater"),
        Col.Array<long>(18, "CostDay", w => w.City!.Buildings.CostDay, _bldPairN, "2 * HighWater"),
        Col.Array<short>(19, "LossDays", w => w.City!.Buildings.LossDays, _bldHw, "HighWater", "0", _bldPool),
        Col.Array<short>(20, "VacantDays", w => w.City!.Buildings.VacantDays, _bldHw, "HighWater", "0", _bldPool),
    })
    {
        After = c =>
        {
            c.W.City!.Buildings.Pool.RecountAlive();
            return LoadStatus.Ok;
        },
        PresentText = "demand.model = residents",
        Present = CityPresent,
    };

    // ---- RESD: жители (пул, поля, план дня, нога), очередь ищущих ----

    private static BlockDef ResidentsCityBlock() => new("RESD", "жители города: пул (поколения, free-list), поля, план дня, нога, очередь ищущих работу (zoning-demand.md §2.4)", new[]
    {
        PoolHighWater(1, _resdPool),
        PoolFreeCount(2, _resdPool),
        Col.Array<int>(3, "FreeList", w => w.City!.Residents.Pool.FreeListArray, w => w.City!.Residents.Pool.FreeCount, "FreeCount"),
        Col.Array<int>(4, "Generation", w => w.City!.Residents.Pool.GenerationArray, _resdHw, "HighWater"),
        Col.Array<int>(5, "Home", w => w.City!.Residents.Home, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(6, "Work", w => w.City!.Residents.Work, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(7, "NextHome", w => w.City!.Residents.NextHome, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(8, "NextWork", w => w.City!.Residents.NextWork, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<byte>(9, "Flags", w => w.City!.Residents.Flags, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<byte>(10, "State", w => w.City!.Residents.State, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<long>(11, "StateTick", w => w.City!.Residents.StateTick, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<long>(12, "EvTick", w => w.City!.Residents.EvTick, _resdHw, "HighWater", "-1", _resdPool, -1L),
        Col.Array<byte>(13, "EvKind", w => w.City!.Residents.EvKind, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<byte>(14, "PlanCount", w => w.City!.Residents.PlanCount, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<byte>(15, "PlanCursor", w => w.City!.Residents.PlanCursor, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<byte>(16, "PlanKind", w => w.City!.Residents.PlanKind, _resdPlanN, "HighWater * 3"),
        Col.Array<byte>(17, "PlanMode", w => w.City!.Residents.PlanMode, _resdPlanN, "HighWater * 3"),
        Col.Array<int>(18, "PlanDest", w => w.City!.Residents.PlanDest, _resdPlanN, "HighWater * 3", "-1"),
        Col.Array<ushort>(19, "PlanSlot", w => w.City!.Residents.PlanSlot, _resdPlanN, "HighWater * 3"),
        Col.Array<byte>(20, "LegMode", w => w.City!.Residents.LegMode, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<ushort>(21, "LegO", w => w.City!.Residents.LegO, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<ushort>(22, "LegD", w => w.City!.Residents.LegD, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<long>(23, "LegReq", w => w.City!.Residents.LegReq, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<int>(24, "LegWalk", w => w.City!.Residents.LegWalk, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<int>(25, "LegWait", w => w.City!.Residents.LegWait, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<int>(26, "LegRide", w => w.City!.Residents.LegRide, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<int>(27, "Agent", w => w.City!.Residents.Agent, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(28, "AgentGen", w => w.City!.Residents.AgentGen, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<long>(29, "Account", w => w.City!.Residents.Account, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<long>(30, "IncomeEwma", w => w.City!.Residents.IncomeEwma, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<short>(31, "SeekDays", w => w.City!.Residents.SeekDays, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<short>(32, "EvictDays", w => w.City!.Residents.EvictDays, _resdHw, "HighWater", "0", _resdPool),
        Col.Array<int>(33, "Seq", w => w.City!.Residents.Seq, _resdHw, "HighWater", "0", _resdPool),
        Col.Scalar<int>(34, "NextSeq", w => w.City!.Residents.NextSeq, (c, v) => c.W.City!.Residents.NextSeq = v),
        RingCount<SeekEntry>(35, "SeekCount", w => w.City!.Residents.SeekQueue),
        Col.RingField<SeekEntry>(36, "SeekIndex", w => w.City!.Residents.SeekQueue, "SeekCount", nameof(SeekEntry.Index)),
        Col.RingField<SeekEntry>(37, "SeekGeneration", w => w.City!.Residents.SeekQueue, "SeekCount", nameof(SeekEntry.Generation)),
        Col.Array<int>(38, "LegDest", w => w.City!.Residents.LegDest, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(39, "LegStopA", w => w.City!.Residents.LegStopA, _resdHw, "HighWater", "-1", _resdPool, -1),
        Col.Array<int>(40, "LegStopB", w => w.City!.Residents.LegStopB, _resdHw, "HighWater", "-1", _resdPool, -1),
    })
    {
        After = c =>
        {
            c.W.City!.Residents.Pool.RecountAlive();
            return LoadStatus.Ok;
        },
        PresentText = "demand.model = residents",
        Present = CityPresent,
    };

    // ---- SKIM: скимы, поправки агрегата, накопители периода, LS_ref ----

    private static BlockDef SkimsBlock() => new("SKIM", "скимы: стоимость пар ячеек по способу и периоду, K агрегата, накопители наблюдений (отсортированные), LS_ref (zoning-demand.md §5)", new[]
    {
        Col.Array<int>(1, "Skim", w => w.City!.Skims.Skim, w => w.City!.Skims.Skim.Length, "NC * NC * 3 * P"),
        Col.Array<int>(2, "K", w => w.City!.Skims.K, w => w.City!.Skims.K.Length, "NC * NC * P"),
        Col.Scalar<int>(3, "LsRef", w => w.City!.Skims.LsRef, (c, v) => c.W.City!.Skims.LsRef = v),
        Col.Flag(4, "LsRefSet", w => w.City!.Skims.LsRefSet, (c, v) => c.W.City!.Skims.LsRefSet = v),
        Col.Scalar<long>(5, "CurPeriod", w => w.City!.Skims.CurPeriod, (c, v) => c.W.City!.Skims.CurPeriod = v),
        Col.Scalar<int>(6, "UpdateCursor", w => w.City!.Skims.UpdateCursor, (c, v) => c.W.City!.Skims.UpdateCursor = v),
        ObsColumn<int>(7, "ObsKey", 1, (c, a) => c.ObsKeys = a, w => w.City!.Skims.Current, 0),
        ObsColumn<long>(8, "ObsSum", 1, (c, a) => c.ObsSums = a, w => w.City!.Skims.Current, 1),
        ObsColumn<int>(9, "ObsCount", 1, (c, a) => c.ObsCounts = a, w => w.City!.Skims.Current, 2),
        FrozenColumn<int>(10, "FrozenKey", 2, w => w.City!.Skims.FrozenKey),
        FrozenColumn<long>(11, "FrozenSum", 2, w => w.City!.Skims.FrozenSum),
        FrozenColumn<int>(12, "FrozenCount", 2, w => w.City!.Skims.FrozenCount),
        Col.Array<int>(13, "LsC", w => w.City!.Skims.LsC, w => w.City!.Skims.LsC.Length, "NC * 3"),
        Col.Array<int>(14, "LsJobs", w => w.City!.Skims.LsJobs, w => w.City!.Skims.LsJobs.Length, "NC"),
    })
    {
        After = c =>
        {
            // Накопитель периода: записи пришли отсортированными, вставка в таблицу. Ключ вне диапазона или переполнение - порча.
            Skims s = c.W.City!.Skims;
            int n = c.ObsKeys?.Length ?? 0;
            s.Current.Clear();
            int max = s.Skim.Length;
            for (int i = 0; i < n; i++)
            {
                if ((uint)c.ObsKeys![i] >= (uint)max || c.ObsCounts![i] < 1 || !s.Current.Insert(c.ObsKeys[i], c.ObsSums![i], c.ObsCounts[i]))
                {
                    return LoadStatus.InvalidValue;
                }
            }

            c.ObsKeys = null;
            c.ObsSums = null;
            c.ObsCounts = null;
            return s.UpdateCursor >= 0 && s.UpdateCursor <= s.FrozenN ? LoadStatus.Ok : LoadStatus.InvalidValue;
        },
        PresentText = "demand.model = residents",
        Present = CityPresent,
    };

    /// <summary>Столбец накопителя периода: отсортированные записи (длина свободная, группа <paramref name="group"/>), при загрузке - во временный массив контекста.</summary>
    private static ColumnDef ObsColumn<T>(uint id, string name, int group, Action<LoadContext, T[]> assign, Func<World, ObsTable> table, int which)
        where T : unmanaged =>
        Col.Custom(
            id,
            name,
            Col.TypeOf(typeof(T)),
            System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
            "ObsCount",
            "0",
            w => table(w).Count,
            (w, first, n, dst) =>
            {
                ObsTable t = table(w);
                int cnt = t.Count;
                var keys = new int[cnt];
                var sums = new long[cnt];
                var counts = new int[cnt];
                t.CopySorted(keys, sums, counts);
                switch (which)
                {
                    case 0:
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<int>(keys, (int)first, n)).CopyTo(dst);
                        break;
                    case 1:
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<long>(sums, (int)first, n)).CopyTo(dst);
                        break;
                    default:
                        System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<int>(counts, (int)first, n)).CopyTo(dst);
                        break;
                }
            },
            (c, n, src) =>
            {
                if (n > c.W.City!.Skims.Current.MaxEntries)
                {
                    return LoadStatus.CapacityExceeded;
                }

                var a = new T[n];
                src.CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.AsSpan()));
                assign(c, a);
                return LoadStatus.Ok;
            },
            group: group);

    /// <summary>Столбец слепка прошлого периода: свободная длина (группа), хранится в предвыделенных массивах; <c>FrozenN</c> = длина группы.</summary>
    private static ColumnDef FrozenColumn<T>(uint id, string name, int group, Func<World, T[]> array)
        where T : unmanaged =>
        Col.Custom(
            id,
            name,
            Col.TypeOf(typeof(T)),
            System.Runtime.CompilerServices.Unsafe.SizeOf<T>(),
            "FrozenN",
            "0",
            w => w.City!.Skims.FrozenN,
            (w, first, n, dst) => System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<T>(array(w), (int)first, n)).CopyTo(dst),
            (c, n, src) =>
            {
                T[] a = array(c.W);
                if (n > a.Length)
                {
                    return LoadStatus.CapacityExceeded;
                }

                src.CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(new Span<T>(a, 0, (int)n)));
                c.W.City!.Skims.FrozenN = (int)n;
                return LoadStatus.Ok;
            },
            group: group);

    // ---- LEDG: деньги, потоки, курсоры дневных проходов, суточные накопители, загрузки клеток ----

    private static BlockDef LedgerBlock() => new("LEDG", "деньги и сутки: External, City, потоки, курсоры дневных проходов, спрос, накопители, загрузки клеток (zoning-demand.md §7.1, §9.1)", new[]
    {
        Col.Scalar<long>(1, "External", w => w.City!.Ledger.External, (c, v) => c.W.City!.Ledger.External = v),
        Col.Scalar<long>(2, "City", w => w.City!.Ledger.City, (c, v) => c.W.City!.Ledger.City = v),
        Col.Array<long>(3, "FlowDay", w => w.City!.Ledger.FlowDay, static _ => Ledger.FlowCount, "13"),
        Col.Array<long>(4, "FlowYesterday", w => w.City!.Ledger.FlowYesterday, static _ => Ledger.FlowCount, "13"),
        Col.Array<long>(5, "FlowTotal", w => w.City!.Ledger.FlowTotal, static _ => Ledger.FlowCount, "13"),
        Col.Scalar<int>(6, "CursorResidents", w => w.City!.CursorResidents, (c, v) => c.W.City!.CursorResidents = v),
        Col.Scalar<int>(7, "CursorBuildings", w => w.City!.CursorBuildings, (c, v) => c.W.City!.CursorBuildings = v),
        Col.Scalar<int>(8, "CursorAccess", w => w.City!.CursorAccess, (c, v) => c.W.City!.CursorAccess = v),
        Col.Scalar<int>(9, "DemandR", w => w.City!.DemandR, (c, v) => c.W.City!.DemandR = v),
        Col.Scalar<int>(10, "DemandC", w => w.City!.DemandC, (c, v) => c.W.City!.DemandC = v),
        Col.Scalar<int>(11, "DemandI", w => w.City!.DemandI, (c, v) => c.W.City!.DemandI = v),
        Col.Array<int>(12, "ToursDay", w => w.City!.ToursDay, static _ => 2, "2"),
        Col.Array<int>(13, "LegsDay", w => w.City!.LegsDay, static _ => 2 * Skims.Modes, "6"),
        Col.Array<long>(14, "LegCostDay", w => w.City!.LegCostDay, static _ => 2 * Skims.Modes, "6"),
        Col.Array<int>(15, "CellLoads", w => w.City!.Loads.L, w => w.City!.Loads.L.Length, "NC * P * 2"),
    })
    {
        PresentText = "demand.model = residents",
        Present = CityPresent,
    };

    // ---- PARM: режим жителей (world_version 3); ids продолжают 112 ----

    private static ColumnDef[] CityParams() => new[]
    {
        Col.Scalar<int>(113, "PedSource", w => (int)w.PedCfg.Source, (c, v) => c.Desc.Peds.Source = (PedSource)v),
        Col.Scalar<int>(114, "CityDayTicks", w => w.Town.Calendar.DayTicks, (c, v) => c.Desc.Town.Calendar.DayTicks = v),
        Col.Scalar<int>(115, "CityLotFrontageMm", w => w.Town.Zoning.LotFrontageMm, (c, v) => c.Desc.Town.Zoning.LotFrontageMm = v),
        Col.Scalar<int>(116, "CityLotDepthMm", w => w.Town.Zoning.LotDepthMm, (c, v) => c.Desc.Town.Zoning.LotDepthMm = v),
        Col.Scalar<int>(117, "CityNodeClearMm", w => w.Town.Zoning.NodeClearMm, (c, v) => c.Desc.Town.Zoning.NodeClearMm = v),
        Col.Flag(118, "CityGrowth", w => w.Town.Zoning.Growth, (c, v) => c.Desc.Town.Zoning.Growth = v),
        Col.Scalar<int>(119, "CityMaxNewBuildingsPerDay", w => w.Town.Zoning.MaxNewBuildingsPerDay, (c, v) => c.Desc.Town.Zoning.MaxNewBuildingsPerDay = v),
        Col.Flag(120, "CityZoningAuto", w => w.Town.Zoning.Auto, (c, v) => c.Desc.Town.Zoning.Auto = v),
        Col.Scalar<int>(121, "CityCCenterShareQ16", w => w.Town.Zoning.CCenterShareQ16, (c, v) => c.Desc.Town.Zoning.CCenterShareQ16 = v),
        Col.Scalar<int>(122, "CityIEdgeShareQ16", w => w.Town.Zoning.IEdgeShareQ16, (c, v) => c.Desc.Town.Zoning.IEdgeShareQ16 = v),
        Col.Scalar<int>(123, "CityUnzonedShareQ16", w => w.Town.Zoning.UnzonedShareQ16, (c, v) => c.Desc.Town.Zoning.UnzonedShareQ16 = v),
        Col.Scalar<int>(124, "CityMaxPaintM2", w => w.Town.Zoning.MaxPaintM2, (c, v) => c.Desc.Town.Zoning.MaxPaintM2 = v),
        Col.Scalar<int>(125, "CityInitialFillQ16", w => w.Town.Residents.InitialFillQ16, (c, v) => c.Desc.Town.Residents.InitialFillQ16 = v),
        Col.Scalar<int>(126, "CityCarOwnershipQ16", w => w.Town.Residents.CarOwnershipQ16, (c, v) => c.Desc.Town.Residents.CarOwnershipQ16 = v),
        Col.Scalar<int>(127, "CityLabourShareQ16", w => w.Town.Residents.LabourShareQ16, (c, v) => c.Desc.Town.Residents.LabourShareQ16 = v),
        Col.Scalar<int>(128, "CityTripExecution", w => (int)w.Town.Residents.TripExecution, (c, v) => c.Desc.Town.Residents.TripExecution = (TripExecution)v),
        Col.Scalar<int>(129, "CitySeekMaxDays", w => w.Town.Residents.SeekMaxDays, (c, v) => c.Desc.Town.Residents.SeekMaxDays = v),
        Col.Scalar<int>(130, "CityEvictMaxDays", w => w.Town.Residents.EvictMaxDays, (c, v) => c.Desc.Town.Residents.EvictMaxDays = v),
        Col.Scalar<int>(131, "CityWaitMaxTicks", w => w.Town.Residents.WaitMaxTicks, (c, v) => c.Desc.Town.Residents.WaitMaxTicks = v),
        Col.Scalar<int>(132, "CityMaxRelocatePerDayQ16", w => w.Town.Residents.MaxRelocatePerDayQ16, (c, v) => c.Desc.Town.Residents.MaxRelocatePerDayQ16 = v),
        Col.Scalar<int>(133, "CityMaxJobSwitchPerDayQ16", w => w.Town.Residents.MaxJobSwitchPerDayQ16, (c, v) => c.Desc.Town.Residents.MaxJobSwitchPerDayQ16 = v),
        Col.Scalar<int>(134, "CityMaxImmigrantsPerDayQ16", w => w.Town.Residents.MaxImmigrantsPerDayQ16, (c, v) => c.Desc.Town.Residents.MaxImmigrantsPerDayQ16 = v),
        Col.Scalar<int>(135, "CityPlanSlot", w => w.Town.Residents.PlanSlot, (c, v) => c.Desc.Town.Residents.PlanSlot = v),
        Col.Scalar<int>(136, "CityShopSlots", w => w.Town.Residents.ShopSlots, (c, v) => c.Desc.Town.Residents.ShopSlots = v),
        Col.Scalar<int>(137, "CityHomeMinSlots", w => w.Town.Residents.HomeMinSlots, (c, v) => c.Desc.Town.Residents.HomeMinSlots = v),
        Col.Scalar<int>(138, "CityMatchPerTick", w => w.Town.Residents.MatchPerTick, (c, v) => c.Desc.Town.Residents.MatchPerTick = v),
        Col.Scalar<int>(139, "CityVacateDays", w => w.Town.Residents.VacateDays, (c, v) => c.Desc.Town.Residents.VacateDays = v),
        Col.Scalar<int>(140, "CitySwitchRatioQ8", w => w.Town.Residents.SwitchRatioQ8, (c, v) => c.Desc.Town.Residents.SwitchRatioQ8 = v),
        Col.Scalar<int>(141, "CityRBias", w => w.Town.Residents.RBias, (c, v) => c.Desc.Town.Residents.RBias = v),
        Col.Scalar<int>(142, "CityWagePerShift", w => w.Town.Economy.WagePerShift, (c, v) => c.Desc.Town.Economy.WagePerShift = v),
        Col.Scalar<int>(143, "CityBasketPrice", w => w.Town.Economy.BasketPrice, (c, v) => c.Desc.Town.Economy.BasketPrice = v),
        Col.Scalar<int>(144, "CityGoodsPct", w => w.Town.Economy.GoodsPct, (c, v) => c.Desc.Town.Economy.GoodsPct = v),
        Col.Scalar<int>(145, "CityImportPct", w => w.Town.Economy.ImportPct, (c, v) => c.Desc.Town.Economy.ImportPct = v),
        Col.Scalar<int>(146, "CityExportPerShift", w => w.Town.Economy.ExportPerShift, (c, v) => c.Desc.Town.Economy.ExportPerShift = v),
        Col.Scalar<int>(147, "CityInitialResidentBalance", w => w.Town.Economy.InitialResidentBalance, (c, v) => c.Desc.Town.Economy.InitialResidentBalance = v),
        Col.Scalar<int>(148, "CityInitialFirmBalance", w => w.Town.Economy.InitialFirmBalance, (c, v) => c.Desc.Town.Economy.InitialFirmBalance = v),
        Col.Scalar<int>(149, "CityConstructCost", w => w.Town.Economy.ConstructCost, (c, v) => c.Desc.Town.Economy.ConstructCost = v),
        Col.Scalar<int>(150, "CityHireMarginPct", w => w.Town.Economy.HireMarginPct, (c, v) => c.Desc.Town.Economy.HireMarginPct = v),
        Col.Scalar<int>(151, "CityLossDaysFire", w => w.Town.Economy.LossDaysFire, (c, v) => c.Desc.Town.Economy.LossDaysFire = v),
        Col.Scalar<int>(152, "CityLossDaysClose", w => w.Town.Economy.LossDaysClose, (c, v) => c.Desc.Town.Economy.LossDaysClose = v),
        Col.Scalar<int>(153, "CityEwmaShift", w => w.Town.Economy.EwmaShift, (c, v) => c.Desc.Town.Economy.EwmaShift = v),
        Col.Scalar<int>(154, "CityCTargetPct", w => w.Town.Economy.CTargetPct, (c, v) => c.Desc.Town.Economy.CTargetPct = v),
        Col.Scalar<int>(155, "CityThetaMode64", w => w.Town.Choice.ThetaMode64, (c, v) => c.Desc.Town.Choice.ThetaMode64 = v),
        Col.Scalar<int>(156, "CityThetaDest64", w => w.Town.Choice.ThetaDest64, (c, v) => c.Desc.Town.Choice.ThetaDest64 = v),
        Col.Scalar<int>(157, "CityThetaTime64", w => w.Town.Choice.ThetaTime64, (c, v) => c.Desc.Town.Choice.ThetaTime64 = v),
        Col.Scalar<int>(158, "CityThetaFreq64", w => w.Town.Choice.ThetaFreq64, (c, v) => c.Desc.Town.Choice.ThetaFreq64 = v),
        Col.Scalar<int>(159, "CityBetaFreqQ8", w => w.Town.Choice.BetaFreqQ8, (c, v) => c.Desc.Town.Choice.BetaFreqQ8 = v),
        Col.Scalar<int>(160, "CityAscFreqDs", w => w.Town.Choice.AscFreqDs, (c, v) => c.Desc.Town.Choice.AscFreqDs = v),
        Col.Scalar<int>(161, "CityGammaFreqDs", w => w.Town.Choice.GammaFreqDs, (c, v) => c.Desc.Town.Choice.GammaFreqDs = v),
        Col.Scalar<int>(162, "CityAscNonWorkerDs", w => w.Town.Choice.AscNonWorkerDs, (c, v) => c.Desc.Town.Choice.AscNonWorkerDs = v),
        Col.Scalar<int>(163, "CityFreqMax", w => w.Town.Choice.FreqMax, (c, v) => c.Desc.Town.Choice.FreqMax = v),
        Col.Scalar<int>(164, "CityWWaitQ8", w => w.Town.Choice.WWaitQ8, (c, v) => c.Desc.Town.Choice.WWaitQ8 = v),
        Col.Scalar<int>(165, "CityWWalkQ8", w => w.Town.Choice.WWalkQ8, (c, v) => c.Desc.Town.Choice.WWalkQ8 = v),
        Col.Scalar<int>(166, "CityAscCarDs", w => w.Town.Choice.AscCarDs, (c, v) => c.Desc.Town.Choice.AscCarDs = v),
        Col.Scalar<int>(167, "CityAscBusDs", w => w.Town.Choice.AscBusDs, (c, v) => c.Desc.Town.Choice.AscBusDs = v),
        Col.Scalar<int>(168, "CityAscWalkDs", w => w.Town.Choice.AscWalkDs, (c, v) => c.Desc.Town.Choice.AscWalkDs = v),
        Col.Scalar<int>(169, "CitySdeQ8", w => w.Town.Choice.SdeQ8, (c, v) => c.Desc.Town.Choice.SdeQ8 = v),
        Col.Scalar<int>(170, "CitySdlQ8", w => w.Town.Choice.SdlQ8, (c, v) => c.Desc.Town.Choice.SdlQ8 = v),
        Col.Scalar<int>(171, "CityTimeStepSlots", w => w.Town.Choice.TimeStepSlots, (c, v) => c.Desc.Town.Choice.TimeStepSlots = v),
        Col.Scalar<int>(172, "CityDestCandidates", w => w.Town.Choice.DestCandidates, (c, v) => c.Desc.Town.Choice.DestCandidates = v),
        Col.Scalar<int>(173, "CityMaxWalkMm", w => w.Town.Choice.MaxWalkMm, (c, v) => c.Desc.Town.Choice.MaxWalkMm = v),
        Col.Scalar<int>(174, "CitySkimPeriods", w => w.Town.Choice.SkimPeriods, (c, v) => c.Desc.Town.Choice.SkimPeriods = v),
        Col.Scalar<int>(175, "CitySkimShift", w => w.Town.Choice.SkimShift, (c, v) => c.Desc.Town.Choice.SkimShift = v),
        Col.Scalar<int>(176, "CitySkimUpdatePerTick", w => w.Town.Choice.SkimUpdatePerTick, (c, v) => c.Desc.Town.Choice.SkimUpdatePerTick = v),
        Col.Scalar<int>(177, "MaxLots", w => w.Caps.MaxLots, (c, v) => c.Desc.Capacities.MaxLots = v),
        Col.Scalar<int>(178, "MaxBuildings", w => w.Caps.MaxBuildings, (c, v) => c.Desc.Capacities.MaxBuildings = v),
        Col.Scalar<int>(179, "MaxCityResidents", w => w.Caps.MaxCityResidents, (c, v) => c.Desc.Capacities.MaxCityResidents = v),
        Col.Scalar<int>(180, "SkimObsCapacity", w => w.Caps.SkimObsCapacity, (c, v) => c.Desc.Capacities.SkimObsCapacity = v),
        Col.Scalar<float>(181, "CityGridOriginX", w => w.City?.Grid.OriginX ?? 0f, (c, v) => c.CityGridOriginX = v),
        Col.Scalar<float>(182, "CityGridOriginZ", w => w.City?.Grid.OriginZ ?? 0f, (c, v) => c.CityGridOriginZ = v),
        Col.Scalar<int>(183, "CityGridCellsX", w => w.City?.Grid.CellsX ?? 0, (c, v) => c.CityGridCellsX = v),
        Col.Scalar<int>(184, "CityGridCellsZ", w => w.City?.Grid.CellsZ ?? 0, (c, v) => c.CityGridCellsZ = v),
    };

    // ---- WRLD: поток Demand, сутки и режим исполнения; ids продолжают 13 ----

    private static ColumnDef[] CityWorldState() => new[]
    {
        Col.Scalar<ulong>(14, "DemandRngState", w => w.RngDemand.State, (c, v) => c.DemandRngState = v),
        Col.Scalar<ulong>(15, "DemandRngInc", w => w.RngDemand.Inc, (c, v) =>
        {
            if (v == 0UL && c.DemandRngState == 0UL)
            {
                return LoadStatus.Ok;   // сейв без жителей (миграция 2 -> 3): поток остаётся свежим
            }

            if ((v & 1UL) == 0)
            {
                return LoadStatus.InvalidValue;
            }

            c.W.RngDemand = Pcg32.FromState(c.DemandRngState, v);
            return LoadStatus.Ok;
        }),
        CityInt(16, "CityDay", c => c.Day, (c, v) => c.Day = v),
        CityInt(17, "CityExec", c => (int)c.Exec, (c, v) => c.Exec = (TripExecution)v),
        CityInt(18, "CityExecPending", c => c.ExecPending, (c, v) => c.ExecPending = v),
    };

    private static ColumnDef CityInt(uint id, string name, Func<CityWorld, int> get, Action<CityWorld, int> set) =>
        Col.Scalar<int>(id, name, w => w.City == null ? 0 : get(w.City), (c, v) =>
        {
            if (c.W.City == null)
            {
                return v == 0 ? LoadStatus.Ok : LoadStatus.InvalidValue;
            }

            set(c.W.City, v);
            return LoadStatus.Ok;
        });

    // ---- CNTR: счётчики города; ids продолжают 117 ----

    private static ColumnDef[] CityCounters() => new[]
    {
        Col.Scalar<long>(118, "CityEvents", w => w.Counters.CityEvents, (c, v) => c.W.Counters.CityEvents = v),
        Col.Scalar<long>(119, "ResToursPlanned", w => w.Counters.ResToursPlanned, (c, v) => c.W.Counters.ResToursPlanned = v),
        Col.Scalar<long>(120, "ResToursDropped", w => w.Counters.ResToursDropped, (c, v) => c.W.Counters.ResToursDropped = v),
        Col.Scalar<long>(121, "ResToursUnreachable", w => w.Counters.ResToursUnreachable, (c, v) => c.W.Counters.ResToursUnreachable = v),
        Col.Scalar<long>(122, "ResToursCancelled", w => w.Counters.ResToursCancelled, (c, v) => c.W.Counters.ResToursCancelled = v),
        Col.Scalar<long>(123, "ResLegsCar", w => w.Counters.ResLegsCar, (c, v) => c.W.Counters.ResLegsCar = v),
        Col.Scalar<long>(124, "ResLegsBus", w => w.Counters.ResLegsBus, (c, v) => c.W.Counters.ResLegsBus = v),
        Col.Scalar<long>(125, "ResLegsWalk", w => w.Counters.ResLegsWalk, (c, v) => c.W.Counters.ResLegsWalk = v),
        Col.Scalar<long>(126, "ResLegsDone", w => w.Counters.ResLegsDone, (c, v) => c.W.Counters.ResLegsDone = v),
        Col.Scalar<long>(127, "ResLegCostDsTotal", w => w.Counters.ResLegCostDsTotal, (c, v) => c.W.Counters.ResLegCostDsTotal = v),
        Col.Scalar<long>(128, "ResVisits", w => w.Counters.ResVisits, (c, v) => c.W.Counters.ResVisits = v),
        Col.Scalar<long>(129, "ResVisitsUnserved", w => w.Counters.ResVisitsUnserved, (c, v) => c.W.Counters.ResVisitsUnserved = v),
        Col.Scalar<long>(130, "ResLegOverdue", w => w.Counters.ResLegOverdue, (c, v) => c.W.Counters.ResLegOverdue = v),
        Col.Scalar<long>(131, "ResStuck", w => w.Counters.ResStuck, (c, v) => c.W.Counters.ResStuck = v),
        Col.Scalar<long>(132, "ResWaitDepartTicks", w => w.Counters.ResWaitDepartTicks, (c, v) => c.W.Counters.ResWaitDepartTicks = v),
        Col.Scalar<long>(133, "SkimObserved", w => w.Counters.SkimObserved, (c, v) => c.W.Counters.SkimObserved = v),
        Col.Scalar<long>(134, "SkimObsDropped", w => w.Counters.SkimObsDropped, (c, v) => c.W.Counters.SkimObsDropped = v),
        Col.Scalar<long>(135, "SkimUpdates", w => w.Counters.SkimUpdates, (c, v) => c.W.Counters.SkimUpdates = v),
        Col.Scalar<long>(136, "Immigrants", w => w.Counters.Immigrants, (c, v) => c.W.Counters.Immigrants = v),
        Col.Scalar<long>(137, "Emigrants", w => w.Counters.Emigrants, (c, v) => c.W.Counters.Emigrants = v),
        Col.Scalar<long>(138, "ResRelocations", w => w.Counters.ResRelocations, (c, v) => c.W.Counters.ResRelocations = v),
        Col.Scalar<long>(139, "ResHired", w => w.Counters.ResHired, (c, v) => c.W.Counters.ResHired = v),
        Col.Scalar<long>(140, "ResFired", w => w.Counters.ResFired, (c, v) => c.W.Counters.ResFired = v),
        Col.Scalar<long>(141, "ResJobSwitches", w => w.Counters.ResJobSwitches, (c, v) => c.W.Counters.ResJobSwitches = v),
        Col.Scalar<long>(142, "BuildingsBuilt", w => w.Counters.BuildingsBuilt, (c, v) => c.W.Counters.BuildingsBuilt = v),
        Col.Scalar<long>(143, "BuildingsAbandoned", w => w.Counters.BuildingsAbandoned, (c, v) => c.W.Counters.BuildingsAbandoned = v),
        Col.Scalar<long>(144, "BuildingsRemoved", w => w.Counters.BuildingsRemoved, (c, v) => c.W.Counters.BuildingsRemoved = v),
        Col.Scalar<long>(145, "FirmClosures", w => w.Counters.FirmClosures, (c, v) => c.W.Counters.FirmClosures = v),
        Col.Scalar<long>(146, "PaintCommands", w => w.Counters.PaintCommands, (c, v) => c.W.Counters.PaintCommands = v),
        Col.Scalar<long>(147, "ExecSwitches", w => w.Counters.ExecSwitches, (c, v) => c.W.Counters.ExecSwitches = v),
        Col.Scalar<long>(148, "CityDays", w => w.Counters.CityDays, (c, v) => c.W.Counters.CityDays = v),
        Col.Scalar<long>(149, "AggregateLegs", w => w.Counters.AggregateLegs, (c, v) => c.W.Counters.AggregateLegs = v),
    };

    // ---- VEHC: ссылка машины на жителя (id 41) ----

    private static ColumnDef VehicleResident() =>
        Col.Array<int>(41, "Resident", w => w.Vehicles.Resident, _vehicleHw, "HighWater", "-1", _vehPool, -1);
}
