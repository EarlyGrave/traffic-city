using System;
using System.Collections.Generic;
using Sim.Core.Agents;
using Sim.Core.Api;
using Sim.Core.Lod;
using Sim.Core.Metrics;
using Sim.Core.Network;
using Sim.Core.Routing;

namespace Sim.Core.Save;

/// <summary>
/// Загрузка мира из таблиц контейнера (save-format.md §6): <c>Create</c> без генератора. Параметры из <c>PARM/DEMD/VTYP</c> -> выделение массивов по ёмкостям ->
/// заполнение блоков в каноническом порядке (каждый столбец сверяется по типу и длине) -> проверки (<see cref="SaveValidator"/>) -> производные тем же кодом,
/// что в <c>Create</c> -> снапшот и воркеры. Исключений наружу нет: любая ошибка - код <see cref="LoadStatus"/> и место поломки в <see cref="LoadReport"/>.
/// </summary>
internal static class WorldLoader
{
    public static LoadStatus Load(ReadOnlySpan<byte> src, in LoadOptions o, in SimRuntimeOptions rt, out Simulation? sim, out LoadReport report)
    {
        sim = null;
        LoadStatus st = SaveReader.Read(src, SaveSchema.KnownBlockIds(), out SaveTables? t, out report);
        return st != LoadStatus.Ok || t == null ? st : FromTables(t, in o, in rt, out sim, ref report);
    }

    public static LoadStatus Load(SaveSource src, in LoadOptions o, in SimRuntimeOptions rt, out Simulation? sim, out LoadReport report)
    {
        sim = null;
        LoadStatus st = SaveReader.Read(src, SaveSchema.KnownBlockIds(), out SaveTables? t, out report);
        return st != LoadStatus.Ok || t == null ? st : FromTables(t, in o, in rt, out sim, ref report);
    }

    private static LoadStatus Fail(ref LoadReport r, LoadStatus s, uint block = 0, uint column = 0, long row = 0)
    {
        r.Status = s;
        r.FailedBlock = block;
        r.FailedColumn = column;
        r.FailedRow = row;
        return s;
    }

    private static LoadStatus FromTables(SaveTables t, in LoadOptions o, in SimRuntimeOptions rt, out Simulation? sim, ref LoadReport r)
    {
        sim = null;
        SaveHeaderInfo h = t.Header;
        r.FileWorldVersion = h.WorldVersion;
        if (h.WorldVersion != SaveSchema.WorldVersion)
        {
            LoadStatus ms = Migrations.Apply(t, h.WorldVersion, SaveSchema.WorldVersion, Migrations.Steps, out int applied);
            r.MigrationsApplied = applied;
            if (ms != LoadStatus.Ok)
            {
                return Fail(ref r, ms);
            }

            h = t.Header;
        }

        var c = new LoadContext(in o, in rt);
        LoadStatus st = LoadBlocks(t, c, ref r);
        if (st == LoadStatus.Ok)
        {
            st = Finish(t, c, in h, ref r);
            if (st != LoadStatus.Ok)
            {
                Fail(ref r, st, c.FailBlock, c.FailColumn, c.FailRow);
            }
        }

        if (st != LoadStatus.Ok)
        {
            c.W?.Dispose();   // частично созданный мир освобождается (воркеры ещё не запущены)
            return st;
        }

        c.W.StartWorkers(rt.WorkerThreads);
        sim = Simulation.Wrap(c.W);
        r.Status = LoadStatus.Ok;
        return LoadStatus.Ok;
    }

    private static LoadStatus LoadBlocks(SaveTables t, LoadContext c, ref LoadReport r)
    {
        foreach (BlockDef b in SaveSchema.Blocks)
        {
            int idx = t.FindBlock(b.Id);
            ExpectedPresent(b, c, out bool expected);
            if (idx < 0)
            {
                if (expected)
                {
                    return Fail(ref r, LoadStatus.MissingBlock, b.Id);
                }

                continue;
            }

            if (!expected || (t.BlockFlagsOf(idx) & (BlockFlags.Host | BlockFlags.Kept)) != 0)
            {
                return Fail(ref r, LoadStatus.InvalidValue, b.Id);
            }

            int schema = t.BlockSchema(idx);
            if (schema != b.Schema)
            {
                return Fail(ref r, schema > b.Schema ? LoadStatus.TooNew : LoadStatus.MigrationFailed, b.Id);
            }

            if (!b.PreWorld && c.W == null)
            {
                LoadStatus cs = CreateWorld(c);
                if (cs != LoadStatus.Ok)
                {
                    return Fail(ref r, cs, b.Id);
                }
            }

            if (b.Before != null)
            {
                LoadStatus bs = b.Before(c);
                if (bs != LoadStatus.Ok)
                {
                    return Fail(ref r, bs, b.Id, c.FailColumn, c.FailRow);
                }
            }

            LoadStatus st = LoadBlock(t, idx, b, c, ref r);
            if (st != LoadStatus.Ok)
            {
                return st;
            }

            if (b.After != null)
            {
                LoadStatus asx = b.After(c);
                if (asx != LoadStatus.Ok)
                {
                    return Fail(ref r, asx, b.Id, c.FailColumn, c.FailRow);
                }
            }
        }

        // Блоки хоста и kept (непрозрачные): остаются в мире для хоста и следующего сейва.
        CollectOpaque(t, c);
        return LoadStatus.Ok;
    }

    /// <summary>Нужен ли блок: обязательные - всегда; условные - по уже прочитанным параметрам (<c>PARM</c> идёт первым).</summary>
    private static void ExpectedPresent(BlockDef b, LoadContext c, out bool expected)
    {
        expected = true;
        switch (b.Name)
        {
            case "OVLY":
                expected = c.Routing.Levels > 0;
                break;
            case "LODS":
                expected = c.Lod.Mode != LodMode.Off;
                break;
            case "QMSO":
                expected = c.Lod.Mode != LodMode.Off && c.Lod.MesoModel == MesoModel.Queue;
                break;
            case "BTYP":
            case "LOTS":
            case "BLDG":
            case "RESD":
            case "SKIM":
            case "LEDG":
                expected = c.Desc.Demand.Model == DemandModel.Residents;
                break;
            case "PEDS":
            case "PPTH":
            case "RESI":
            case "XING":
            case "PQUE":
                expected = c.Desc.Peds.Enabled;
                break;
        }
    }

    private static LoadStatus LoadBlock(SaveTables t, int idx, BlockDef b, LoadContext c, ref LoadReport r)
    {
        // Неизвестный столбец в известном блоке - порча (как неизвестный ключ JSON).
        int fileColumns = t.ColumnCountOf(idx);
        for (int k = 0; k < fileColumns; k++)
        {
            uint fid = t.GetColumn(idx, k).Id;
            bool known = false;
            for (int j = 0; j < b.Columns.Length && !known; j++)
            {
                known = b.Columns[j].Id == fid;
            }

            if (!known)
            {
                return Fail(ref r, LoadStatus.UnknownColumn, b.Id, fid);
            }
        }

        Array.Fill(c.Group, -1L);
        foreach (ColumnDef col in b.Columns)
        {
            int ci = t.FindColumn(idx, col.Id);
            if (ci < 0)
            {
                if (col.Optional)
                {
                    continue;
                }

                return Fail(ref r, LoadStatus.BadColumn, b.Id, col.Id);
            }

            SaveColumnDesc d = t.GetColumn(idx, ci);
            if (d.Type != col.Type || d.ElemSize != col.ElemSize)
            {
                return Fail(ref r, LoadStatus.BadColumn, b.Id, col.Id);
            }

            long expected;
            if (col.Group > 0)
            {
                expected = c.Group[col.Group];
                if (expected < 0)
                {
                    c.Group[col.Group] = expected = d.Count;
                }
            }
            else
            {
                expected = b.PreWorld ? 1 : col.Count(c.W);
            }

            if (d.Count != expected)
            {
                return Fail(ref r, LoadStatus.BadColumn, b.Id, col.Id, d.Count);
            }

            t.TryGetColumnBytes(idx, col.Id, out ReadOnlySpan<byte> bytes);
            LoadStatus st = col.Store(c, d.Count, bytes);
            if (st != LoadStatus.Ok)
            {
                return Fail(ref r, st, b.Id, col.Id);
            }
        }

        return LoadStatus.Ok;
    }

    private static void CollectOpaque(SaveTables t, LoadContext c)
    {
        var host = new List<HostBlock>();
        var kept = new List<KeptBlock>();
        var data = new List<byte>();
        for (int i = 0; i < t.BlockCount; i++)
        {
            BlockFlags f = t.BlockFlagsOf(i);
            if ((f & BlockFlags.Host) != 0)
            {
                ReadOnlyMemory<byte> p = t.OpaquePayload(i);
                host.Add(new HostBlock { Id = t.BlockId(i), Flags = (ushort)(f & BlockFlags.Critical), Offset = data.Count, Length = p.Length });
                data.AddRange(p.ToArray());
            }
            else if ((f & BlockFlags.Kept) != 0)
            {
                kept.Add(new KeptBlock(t.BlockId(i), (ushort)t.BlockSchema(i), (ushort)f, t.OpaquePayload(i).ToArray()));
            }
        }

        c.W.LoadedHostBlocks = host.ToArray();
        c.W.LoadedHostData = data.ToArray();
        c.W.KeptBlocks = kept.ToArray();
    }

    /// <summary>Создаёт мир по параметрам, прочитанным блоками <c>PARM</c>, <c>DEMD</c>, <c>VTYP</c>.</summary>
    private static LoadStatus CreateWorld(LoadContext c)
    {
        ApplyOverride(ref c.Desc.Capacities, in c.Options.CapacityOverride);
        c.Desc.VehicleTypes = c.Types;
        c.Desc.Town.BuildingTypes = c.BuildingTypes;
        c.Desc.Demand.Zones = c.Zones;
        c.Desc.Demand.OdPairs = c.OdPairs;
        c.Desc.Demand.TypeMix = c.TypeMix.Length > 0 ? c.TypeMix : null!;
        c.Desc.Routing = c.Routing;
        c.Desc.Lod = c.Lod;
        c.Desc.Rules = c.Rules;

        LoadStatus st = SaveValidator.ValidateParams(c);
        if (st != LoadStatus.Ok)
        {
            return st;
        }

        c.Desc.City.Enabled = true;   // построитель сети с сохранённой геометрией полос (нужен BuildRoad после загрузки)
        c.Desc.City.LaneWidth = c.NetLaneWidth;
        c.Desc.City.ConnectorSamples = c.NetConnectorSamples;
        c.W = new World(in c.Desc, c.Rules, c.Routing, c.Types);
        if (WorldBuilder.BuildPeds(c.W, create: false) != CreateStatus.Ok)   // пешеходы: структуры без жителей, состояние - из блоков
        {
            return LoadStatus.CapacityExceeded;
        }

        var grid = new City.CityGrid(c.Lod.CellM, c.CityGridOriginX, c.CityGridOriginZ, c.CityGridCellsX, c.CityGridCellsZ);
        return City.CityBuild.Build(c.W, create: false, in grid) == CreateStatus.Ok ? LoadStatus.Ok : LoadStatus.CapacityExceeded;   // город: хранилища по ёмкостям, состояние - из блоков
    }

    private static void ApplyOverride(ref SimCapacities caps, in SimCapacities ov)
    {
        if (ov.MaxVehicles != 0) { caps.MaxVehicles = ov.MaxVehicles; }
        if (ov.MaxNodes != 0) { caps.MaxNodes = ov.MaxNodes; }
        if (ov.MaxSegments != 0) { caps.MaxSegments = ov.MaxSegments; }
        if (ov.MaxLanes != 0) { caps.MaxLanes = ov.MaxLanes; }
        if (ov.MaxLanePoints != 0) { caps.MaxLanePoints = ov.MaxLanePoints; }
        if (ov.MaxZones != 0) { caps.MaxZones = ov.MaxZones; }
        if (ov.MaxBusRoutes != 0) { caps.MaxBusRoutes = ov.MaxBusRoutes; }
        if (ov.MaxBusStops != 0) { caps.MaxBusStops = ov.MaxBusStops; }
        if (ov.MaxBusStopsPerRoute != 0) { caps.MaxBusStopsPerRoute = ov.MaxBusStopsPerRoute; }
        if (ov.MaxRoutes != 0) { caps.MaxRoutes = ov.MaxRoutes; }
        if (ov.MaxPlanSteps != 0) { caps.MaxPlanSteps = ov.MaxPlanSteps; }
        if (ov.MaxPendingTrips != 0) { caps.MaxPendingTrips = ov.MaxPendingTrips; }
        if (ov.CommandQueueCapacity != 0) { caps.CommandQueueCapacity = ov.CommandQueueCapacity; }
        if (ov.EventCapacity != 0) { caps.EventCapacity = ov.EventCapacity; }
        if (ov.MaxSignalizedNodes != 0) { caps.MaxSignalizedNodes = ov.MaxSignalizedNodes; }
        if (ov.MaxConnectors != 0) { caps.MaxConnectors = ov.MaxConnectors; }
        if (ov.MaxConflicts != 0) { caps.MaxConflicts = ov.MaxConflicts; }
        if (ov.MaxLots != 0) { caps.MaxLots = ov.MaxLots; }
        if (ov.MaxBuildings != 0) { caps.MaxBuildings = ov.MaxBuildings; }
        if (ov.MaxCityResidents != 0) { caps.MaxCityResidents = ov.MaxCityResidents; }
        if (ov.SkimObsCapacity != 0) { caps.SkimObsCapacity = ov.SkimObsCapacity; }
    }

    /// <summary>Конец загрузки: проверки состояния, производные, роутер, снапшот, сверка хэша.</summary>
    private static LoadStatus Finish(SaveTables t, LoadContext c, in SaveHeaderInfo h, ref LoadReport r)
    {
        World w = c.W;
        LoadStatus st = SaveValidator.ValidateRest(c);
        if (st != LoadStatus.Ok)
        {
            c.FailBlock = SaveFormat.FourCc("WRLD"u8);
            return st;
        }

        // NextConn честного сейва равен полному расчёту (Debug-сверка FirstHop); испорченный, но структурно допустимый (соседний connector той же полосы)
        // исполнение в Debug роняло бы. Маршруты и сеть уже загружены: перезаписываем посчитанным (для честного файла значение не меняется).
        VehicleStore veh = w.Vehicles;
        for (int i = 0; i < veh.HighWater; i++)
        {
            int ln = veh.Lane[i];
            if (veh.NextConn[i] >= 0 && ln >= 0 && veh.Pool.IsAlive(i))
            {
                veh.NextConn[i] = RouteNav.NextConnector(w, i, ln);
            }
        }

        // Производные оставшихся хранилищ.
        w.Occupancy.RebuildMasks(w.Net.LaneCount);
        if (w.Net.PedNet != null)
        {
            // Сеть пешеходов - производное (pedestrians.md §2), тем же кодом, что в Create; затем проверки состояния агентов (им нужны индексы рёбер) и их производные.
            if (!w.Net.PedNet.Rebuild(w))
            {
                return LoadStatus.CapacityExceeded;
            }

            st = SaveValidator.ValidatePeds(c);
            if (st != LoadStatus.Ok)
            {
                c.FailBlock = SaveFormat.FourCc("PEDS"u8);
                return st;
            }

            w.Peds!.RestoreDerived(w);
        }

        if (w.City != null)
        {
            st = SaveValidator.ValidateCity(c);
            if (st != LoadStatus.Ok)
            {
                c.FailBlock = SaveFormat.FourCc("BLDG"u8);
                return st;
            }

            w.City.RestoreDerived(w);   // геометрия участков, ячейки, точки доступа, компоненты - тем же кодом, что в Create
        }

        w.Router = new Router(w, w.Graph, w.Overlay, w.Routing.QueryWorkCap);
        for (int lane = 0; lane < w.Net.LaneCount; lane++)
        {
            if (w.Net.OutReserved[lane] != 0f)
            {
                w.Gate.OutTouched[w.Gate.OutTouchedCount++] = lane;   // фаза 4 обнуляет только полосы этого списка
            }
        }

        // Снапшот «сейчас»: текущий буфер заполняет фаза 10, предыдущий = текущий (save-format.md §1).
        long snap = w.Tick;
        SnapshotPhase.RunFor(w, snap);
        int b = (int)(snap & 1);
        int high = w.Vehicles.HighWater;
        Array.Copy(w.Render[b], w.Render[b ^ 1], high);
        w.SnapshotSlots[b ^ 1] = high;
        w.RenderView.MarkWrittenAll(b ^ 1);
        w.Peds?.Snapshot.CopyToPrevious(snap, w.Peds.P.HighWater);

        r.HashChecked = 0;
        r.HashMatched = 0;
        if (h.HashVersion == SaveSchema.HashVersion && r.MigrationsApplied == 0)
        {
            r.HashChecked = 1;
            bool match = StateHash.Compute(w) == h.StateHash;
            r.HashMatched = match ? (byte)1 : (byte)0;
            if (!match && c.Options.StrictHash != 0)
            {
                return LoadStatus.HashMismatch;
            }
        }

        r.KeptBlocks = t.KeptBlocks;
        r.AssetsInManifest = w.AssetManifest.Length;
        r.AssetsMissing = c.AssetsMissing;
        r.AssetsChanged = c.AssetsChanged;
        return LoadStatus.Ok;
    }
}
