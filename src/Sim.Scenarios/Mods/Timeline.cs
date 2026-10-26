using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Sim.Core;
using Sim.Core.Api;
using Sim.Core.Network;

namespace Sim.Scenarios.Mods;

/// <summary>Одна запись таймлайна: команда <see cref="Cmd"/> ставится в очередь перед <c>Step</c>, первый тик которого - <see cref="Tick"/>.</summary>
public readonly record struct TimelineEntry(long Tick, Command Cmd);

/// <summary>Отказ команды таймлайна ядром (<c>CommandRejected</c>), в том числе <see cref="RejectReason.NotSupported"/>.</summary>
public readonly record struct TimelineReject(long Tick, CommandKind Kind, RejectReason Reason, uint Tag);

/// <summary>
/// Таймлайн команд сценария (modding.md §3.5, M9): разбор ключей <c>timeline</c> / <c>api_version</c>.
/// Ядро про файлы не знает: подачу в <see cref="Simulation"/> делает <see cref="TimelineRunner"/> (хост).
/// </summary>
public static class Timeline
{
    /// <summary>Контекст разбора: ссылки на ассеты (<c>road</c>, <c>vehicle_type</c>) переводятся в индексы.</summary>
    internal sealed class Context
    {
        public required ScenarioLoader.TypeRefs Types { get; init; }

        /// <summary>Тип дороги по ссылке (только v3; v2 - <c>null</c>).</summary>
        public Func<string, string, RoadEntry>? Road { get; init; }
    }

    /// <summary>Проверка <c>api_version</c> (число или строка semver): MAJOR должен совпасть с <see cref="CoreInfo.ApiVersion"/>.</summary>
    internal static void CheckApiVersion(JsonElement e, string path)
    {
        int major;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out int n) && n >= 0)
        {
            major = n;
        }
        else if (e.ValueKind == JsonValueKind.String
            && int.TryParse(e.GetString()!.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out major))
        {
            // MAJOR из "1" / "1.0" / "1.2.3"
        }
        else
        {
            throw new ScenarioException($"{path}: expected non-negative integer or semver string", ModError.BadType);
        }

        if (major != CoreInfo.ApiVersion)
        {
            throw new ScenarioException(
                $"{path}: {ModError.ApiVersionMismatch} (scenario {major}, core {CoreInfo.ApiVersion})", ModError.ApiVersionMismatch);
        }
    }

    internal static TimelineEntry[] Parse(JsonObjectReader root, Context ctx)
    {
        bool hasTimeline = root.Has("timeline");
        if (root.Has("api_version"))
        {
            CheckApiVersion(root.Raw("api_version"), root.ChildPath("api_version"));
        }
        else if (hasTimeline)
        {
            throw new ScenarioException("$.api_version: required key is missing (timeline needs it)", ModError.MissingKey);
        }

        if (!hasTimeline)
        {
            return [];
        }

        JsonElement[] items = root.ReqArray("timeline");
        string tp = root.ChildPath("timeline");
        var list = new TimelineEntry[items.Length];
        long prev = 0;
        for (int i = 0; i < items.Length; i++)
        {
            string ip = JsonObjectReader.Index(tp, i);
            var er = new JsonObjectReader(items[i], ip);
            long tick = er.Int("tick", min: 0);
            if (tick < prev)
            {
                throw new ScenarioException($"{ip}.tick: {ModError.TimelineUnsorted} ({tick} after {prev})", ModError.TimelineUnsorted);
            }

            prev = tick;
            JsonObjectReader cr = er.ReqObject("cmd");
            Command cmd = ParseCommand(cr, ctx);
            er.Finish();
            list[i] = new TimelineEntry(tick, cmd);
        }

        return list;
    }

    // ---------------------------------------------------------------- команды (JSON-форма, ADR-0023)

    private static Command ParseCommand(JsonObjectReader r, Context ctx)
    {
        string kind = r.Str("kind");
        uint tag = (uint)r.Num("tag", 0, 0, uint.MaxValue);
        Command c = kind switch
        {
            "build_road" => BuildRoad(r, ctx, tag),
            "spawn_trip" => SpawnTrip(r, ctx, tag),
            "set_signal_plan" => SignalPlan(r, tag),
            "create_bus_route" => Command.MakeCreateBusRoute(new CreateBusRouteCmd
            {
                HeadwayTicks = r.Int("headway_ticks", min: 1),
                VehicleType = VehicleType(r, "vehicle_type", ctx, -1),
                Flags = r.Int("flags", 0),
                RestoreOf = Ref(r, "restore_of"),
            }, tag),
            "append_bus_stop" => Command.MakeAppendBusStop(new AppendBusStopCmd
            {
                Route = r.Int("route", min: 0),
                Lane = r.Int("lane", min: 0),
                S = F(r, "s", 0),
                Kind = Enum(r, "stop_kind", BusStopKind.Curb, ("curb", BusStopKind.Curb), ("bay", BusStopKind.Bay)),
            }, tag),
            "spawn_bus" => Command.MakeSpawnBus(r.Int("route", min: 0), tag),
            "select_vehicle" => Command.MakeSelectVehicle(new VehicleHandle(r.Int("index", min: 0), r.Int("generation", 0)), tag),
            "clear_selection" => Command.MakeClearSelection(tag),
            "set_target_vehicle_count" => Command.MakeSetTargetVehicleCount(r.Int("count", min: 0), tag),
            "set_ped_target" => Command.MakeSetPedTarget(r.Int("count", min: 0), tag),
            "set_node_control" => NodeControlCmd(r, tag),
            "paint_zone" => PaintZone(r, tag),
            "set_trip_execution" => Command.MakeSetTripExecution(
                Enum(r, "mode", TripExecution.Agents, ("agents", TripExecution.Agents), ("aggregate", TripExecution.Aggregate)), tag),
            "remove_road" => Command.MakeRemoveRoad(new RemoveRoadCmd
            {
                Seg = Ref(r, "seg", required: true),
                Flags = r.Bool("keep_nodes", false) ? RemoveRoadFlags.KeepNodes : 0,
            }, tag),
            "upgrade_road" => UpgradeRoad(r, tag),
            "split_road" => Command.MakeSplitRoad(new SplitRoadCmd
            {
                Seg = Ref(r, "seg", required: true),
                S = F(r, "s"),
                RestoreNode = Ref(r, "restore_node"),
            }, tag),
            "set_arm_control" => Command.MakeSetArmControl(new SetArmControlCmd
            {
                Node = Ref(r, "node", required: true),
                ArmSlot = B(r, "arm_slot"),
                Control = B(r, "control"),
            }, tag),
            "set_turn_ban" => Command.MakeSetTurnBan(new SetTurnBanCmd
            {
                Node = Ref(r, "node", required: true),
                FromArm = B(r, "from_arm"),
                ToArm = B(r, "to_arm"),
                Banned = B(r, "banned", 1),
            }, tag),
            "set_lane_flags" => Command.MakeSetLaneFlags(new SetLaneFlagsCmd
            {
                Seg = Ref(r, "seg", required: true),
                Dir = B(r, "dir"),
                LaneNo = B(r, "lane_no"),
                Flags = B(r, "flags"),
                Mask = B(r, "mask", 255),
            }, tag),
            "insert_bus_stop" => Command.MakeInsertBusStop(new InsertBusStopCmd
            {
                Route = Ref(r, "route", required: true),
                Position = r.Int("position", -1, min: -1),
                Seg = Ref(r, "seg", required: true),
                Dir = B(r, "dir"),
                LaneNo = B(r, "lane_no"),
                Kind = B(r, "stop_kind"),
                S = F(r, "s", 0),
                RestoreOf = Ref(r, "restore_of"),
            }, tag),
            "remove_bus_stop" => Command.MakeRemoveBusStop(new RemoveBusStopCmd { Stop = Ref(r, "stop", required: true) }, tag),
            "remove_bus_route" => Command.MakeRemoveBusRoute(new RemoveBusRouteCmd { Route = Ref(r, "route", required: true) }, tag),
            "set_bus_route_params" => Command.MakeSetBusRouteParams(new SetBusRouteParamsCmd
            {
                Route = Ref(r, "route", required: true),
                HeadwayTicks = r.Int("headway_ticks", 0, min: 0),
                VehicleType = VehicleType(r, "vehicle_type", ctx, -1),
            }, tag),
            "merge_at_node" => Command.MakeMergeAtNode(new MergeAtNodeCmd
            {
                Node = Ref(r, "node", required: true),
                RestoreSeg = Ref(r, "restore_seg"),
            }, tag),
            _ => throw new ScenarioException($"{r.ChildPath("kind")}: unknown command kind '{kind}'", ModError.BadValue),
        };
        r.Finish();
        return c;
    }

    private static Command BuildRoad(JsonObjectReader r, Context ctx, uint tag)
    {
        var p = new BuildRoadCmd();
        (p.Ax, p.Az) = Point(r, "a");
        (p.Bx, p.Bz) = Point(r, "b");
        bool hasRoad = r.Has("road");
        if (hasRoad)
        {
            if (ctx.Road == null)
            {
                throw new ScenarioException($"{r.ChildPath("road")}: asset refs need format_version 3", ModError.UnknownKey);
            }

            RoadEntry e = ctx.Road(r.Str("road"), r.ChildPath("road"));
            p.Kind = e.Kind;
            p.LanesAB = (byte)e.LanesAB;
            p.LanesBA = (byte)e.LanesBA;
            p.SpeedLimit = e.SpeedLimit;
        }

        // явные поля перекрывают тип дороги; без типа скорость обязательна
        if (r.Has("road_kind") || !hasRoad)
        {
            p.Kind = Enum(r, "road_kind", RoadKind.Street, ("street", RoadKind.Street), ("arterial", RoadKind.Arterial));
        }

        if (r.Has("lanes_ab") || !hasRoad)
        {
            p.LanesAB = B(r, "lanes_ab", 1);
        }

        if (r.Has("lanes_ba") || !hasRoad)
        {
            p.LanesBA = B(r, "lanes_ba", 1);
        }

        if (r.Has("speed_mps") || !hasRoad)
        {
            p.SpeedLimit = F(r, "speed_mps");
        }

        p.Flags = Flags(r, "flags", ("no_connectors", BuildRoadFlags.NoConnectors), ("auto_split", BuildRoadFlags.AutoSplit), ("curve", BuildRoadFlags.Curve));
        if (r.Has("c"))
        {
            (p.Cx, p.Cz) = Point(r, "c");
        }

        p.RestoreOf = Ref(r, "restore_of");
        p.RestoreNodeA = Ref(r, "restore_node_a");
        p.RestoreNodeB = Ref(r, "restore_node_b");
        return Command.MakeBuildRoad(p, tag);
    }

    private static Command SpawnTrip(JsonObjectReader r, Context ctx, uint tag) =>
        Command.MakeSpawnTrip(new SpawnTripCmd
        {
            FromZone = r.Int("from_zone", -1, min: -1),
            ToZone = r.Int("to_zone", min: 0),
            FromLane = r.Int("from_lane", -1, min: -1),
            FromS = F(r, "from_s", 0),
            VehicleType = VehicleType(r, "vehicle_type", ctx, 0),
            Count = r.Int("count", 1, min: 1),
        }, tag);

    private static Command SignalPlan(JsonObjectReader r, uint tag)
    {
        JsonElement[] greens = r.ReqArray("green_ticks");
        if (greens.Length is < 1 or > 8)
        {
            throw new ScenarioException($"{r.ChildPath("green_ticks")}: 1..8 phases expected", ModError.OutOfRange);
        }

        var p = new SetSignalPlanCmd
        {
            Node = r.Int("node", min: 0),
            NodeGen = r.Int("node_gen", 0),
            Mode = Enum(r, "mode", SignalMode.Fixed,
                ("off", SignalMode.Off), ("fixed", SignalMode.Fixed), ("flash_yellow", SignalMode.FlashYellow), ("adaptive", SignalMode.Adaptive)),
            OffsetTicks = U16(r, "offset_ticks"),
            PhaseCount = (byte)greens.Length,
            YellowTicks = U16(r, "yellow_ticks"),
            AllRedTicks = U16(r, "all_red_ticks"),
            MinGreenTicks = U16(r, "min_green_ticks"),
            GapTicks = U16(r, "gap_ticks"),
            ArrowTriggerTicks = U16(r, "arrow_trigger_ticks"),
            ArrowMinTicks = U16(r, "arrow_min_ticks"),
            ArrowMaxTicks = U16(r, "arrow_max_ticks"),
            CycleTicks = U16(r, "cycle_ticks"),
            Flags = r.Bool("arrows", false) ? SignalPlanFlags.Arrows : (byte)0,
        };
        string gp = r.ChildPath("green_ticks");
        ushort G(int i) => i < greens.Length ? (ushort)JsonObjectReader.IntValue(greens[i], JsonObjectReader.Index(gp, i), 0, ushort.MaxValue) : (ushort)0;
        p.Green0 = G(0);
        p.Green1 = G(1);
        p.Green2 = G(2);
        p.Green3 = G(3);
        p.Green4 = G(4);
        p.Green5 = G(5);
        p.Green6 = G(6);
        p.Green7 = G(7);
        return Command.MakeSetSignalPlan(p, tag);
    }

    private static Command NodeControlCmd(JsonObjectReader r, uint tag)
    {
        int node = r.Int("node", min: 0);
        int gen = r.Int("node_gen", 0);
        NodeControl ctl = Enum(r, "control", NodeControl.None, ("none", NodeControl.None), ("yield", NodeControl.Yield),
            ("stop", NodeControl.Stop), ("signal", NodeControl.Signal), ("right_hand_rule", NodeControl.RightHandRule));
        Command c = Command.MakeSetNodeControl(node, ctl, tag);
        c.SetNodeControl.NodeGen = gen;
        return c;
    }

    private static Command PaintZone(JsonObjectReader r, uint tag)
    {
        JsonElement[] rect = r.ReqArray("rect");
        string rp = r.ChildPath("rect");
        if (rect.Length != 4)
        {
            throw new ScenarioException($"{rp}: expected [x0, z0, x1, z1]", ModError.BadValue);
        }

        return Command.MakePaintZone(new PaintZoneCmd
        {
            X0 = (float)JsonObjectReader.NumValue(rect[0], JsonObjectReader.Index(rp, 0)),
            Z0 = (float)JsonObjectReader.NumValue(rect[1], JsonObjectReader.Index(rp, 1)),
            X1 = (float)JsonObjectReader.NumValue(rect[2], JsonObjectReader.Index(rp, 2)),
            Z1 = (float)JsonObjectReader.NumValue(rect[3], JsonObjectReader.Index(rp, 3)),
            ZoneKind = (byte)Enum(r, "zone", LotZone.None,
                ("none", LotZone.None), ("residential", LotZone.Residential), ("commercial", LotZone.Commercial), ("industrial", LotZone.Industrial)),
            Density = B(r, "density", 0),
        }, tag);
    }

    private static Command UpgradeRoad(JsonObjectReader r, uint tag)
    {
        // «не менять»: speed_mps 0, lanes 255, road_kind не задан (флаг KeepKind)
        bool keepKind = !r.Has("road_kind");
        return Command.MakeUpgradeRoad(new UpgradeRoadCmd
        {
            Seg = Ref(r, "seg", required: true),
            SpeedLimit = F(r, "speed_mps", 0),
            LanesAB = B(r, "lanes_ab", 255),
            LanesBA = B(r, "lanes_ba", 255),
            Kind = Enum(r, "road_kind", RoadKind.Street, ("street", RoadKind.Street), ("arterial", RoadKind.Arterial)),
            Flags = keepKind ? (byte)1 : (byte)0,
        }, tag);
    }

    // ---------------------------------------------------------------- поля

    private static float F(JsonObjectReader r, string key, double? def = null) => (float)r.Num(key, def);

    private static byte B(JsonObjectReader r, string key, int def = 0) => (byte)r.Int(key, def, 0, 255);

    private static ushort U16(JsonObjectReader r, string key) => (ushort)r.Int(key, 0, 0, ushort.MaxValue);

    private static (float X, float Z) Point(JsonObjectReader r, string key)
    {
        JsonElement[] a = r.ReqArray(key);
        string p = r.ChildPath(key);
        if (a.Length != 2)
        {
            throw new ScenarioException($"{p}: expected [x, z]", ModError.BadValue);
        }

        return ((float)JsonObjectReader.NumValue(a[0], JsonObjectReader.Index(p, 0)), (float)JsonObjectReader.NumValue(a[1], JsonObjectReader.Index(p, 1)));
    }

    /// <summary>Ссылка на сущность <c>{ "index": i, "generation": g }</c>; без ключа - <see cref="EntityRef.None"/> (поколение 0, «не задано»).</summary>
    private static EntityRef Ref(JsonObjectReader r, string key, bool required = false)
    {
        if (!r.Has(key))
        {
            return required ? throw new ScenarioException($"{r.ChildPath(key)}: required key is missing", ModError.MissingKey) : EntityRef.None;
        }

        JsonObjectReader o = r.ReqObject(key);
        var e = new EntityRef(o.Int("index", min: 0), o.Int("generation", 0, min: 0));
        o.Finish();
        return e;
    }

    private static int VehicleType(JsonObjectReader r, string key, Context ctx, int def) =>
        r.Has(key) ? ctx.Types.Find(r.Str(key), r.ChildPath(key)) : def;

    private static T Enum<T>(JsonObjectReader r, string key, T def, params (string Name, T Value)[] map)
    {
        if (!r.Has(key))
        {
            return def;
        }

        string s = r.Str(key);
        foreach ((string name, T value) in map)
        {
            if (name == s)
            {
                return value;
            }
        }

        throw new ScenarioException($"{r.ChildPath(key)}: unknown value '{s}'", ModError.BadValue);
    }

    private static byte Flags(JsonObjectReader r, string key, params (string Name, byte Bit)[] map)
    {
        if (!r.Has(key))
        {
            return 0;
        }

        JsonElement[] a = r.ReqArray(key);
        string p = r.ChildPath(key);
        byte bits = 0;
        for (int i = 0; i < a.Length; i++)
        {
            string s = a[i].ValueKind == JsonValueKind.String ? a[i].GetString()! : string.Empty;
            bool found = false;
            foreach ((string name, byte bit) in map)
            {
                if (name == s)
                {
                    bits |= bit;
                    found = true;
                }
            }

            if (!found)
            {
                throw new ScenarioException($"{JsonObjectReader.Index(p, i)}: unknown flag '{s}'", ModError.BadValue);
            }
        }

        return bits;
    }
}

/// <summary>
/// Подача таймлайна в симуляцию: <see cref="Step"/> делит шаг на границах записей (команды тика <c>T</c> ставятся перед <c>Step</c>,
/// первый тик которого - <c>T</c>), результат равен ручному <c>Enqueue</c> в те же тики. Записи с <c>tick</c> меньше текущего тика
/// (например, после прогрева) ставятся в очередь при первом же вызове.
/// </summary>
public sealed class TimelineRunner
{
    private readonly IReadOnlyList<TimelineEntry> _entries;
    private readonly List<TimelineReject> _rejects = [];
    private int _next;

    public TimelineRunner(IReadOnlyList<TimelineEntry> entries)
    {
        _entries = entries;
    }

    /// <summary>Сколько записей уже поставлено в очередь.</summary>
    public int Enqueued => _next;

    /// <summary>Записей всего.</summary>
    public int Count => _entries.Count;

    /// <summary>Записей, не принятых очередью (она полна).</summary>
    public int Dropped { get; private set; }

    /// <summary>Отказы ядра за все вызовы <see cref="Step"/> (в том числе <c>NotSupported</c> для инструментов, которых ядро ещё не умеет).</summary>
    public IReadOnlyList<TimelineReject> Rejects => _rejects;

    /// <summary>Ставит в очередь все записи с <c>Tick &lt;= sim.Tick</c>.</summary>
    public void Feed(Simulation sim)
    {
        while (_next < _entries.Count && _entries[_next].Tick <= sim.Tick)
        {
            Command cmd = _entries[_next].Cmd;
            if (!sim.Enqueue(in cmd))
            {
                Dropped++;
            }

            _next++;
        }
    }

    /// <summary>Итоговая строка для консоли: сколько команд поставлено и сколько отклонено ядром, затем по строке на отказ.</summary>
    public void WriteSummary(System.IO.TextWriter output)
    {
        if (_entries.Count == 0)
        {
            return;
        }

        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"timeline: {_next}/{_entries.Count} commands enqueued, {_rejects.Count} rejected, {Dropped} dropped (queue full)"));
        foreach (TimelineReject r in _rejects)
        {
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture, $"timeline: rejected tick={r.Tick} kind={r.Kind} reason={r.Reason} tag={r.Tag}"));
        }
    }

    /// <summary>Тиков до ближайшей непоставленной записи (<c>int.MaxValue</c>, если их нет; 0 - есть просроченная).</summary>
    public int TicksToNext(Simulation sim) =>
        _next >= _entries.Count ? int.MaxValue : (int)Math.Clamp(_entries[_next].Tick - sim.Tick, 0, int.MaxValue);

    /// <summary>Выполняет <paramref name="ticks"/> тиков, останавливаясь на границах таймлайна; отказы команд копятся в <see cref="Rejects"/>.</summary>
    public void Step(Simulation sim, int ticks)
    {
        int left = ticks;
        while (left > 0)
        {
            Feed(sim);
            int n = Math.Min(left, Math.Max(1, TicksToNext(sim)));
            sim.Step(n);
            CollectRejects(sim);
            left -= n;
        }
    }

    /// <summary>Забирает отказы из <c>sim.Events</c> последнего <c>Step</c> (для хостов, которые шагают сами, после <see cref="Feed"/>).</summary>
    public void CollectRejects(Simulation sim)
    {
        foreach (SimEvent e in sim.Events)
        {
            if (e.Kind == SimEventKind.CommandRejected)
            {
                _rejects.Add(new TimelineReject(e.Tick, (CommandKind)e.B, (RejectReason)e.A, e.Tag));
            }
        }
    }
}
