using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Sim.Core.Api;
using Sim.Scenarios;

namespace FfiSmoke;

/// <summary>
/// Сверка C#-ядра с Rust cdylib в одном процессе (rust-pilot.md §5.3, R6). Выход 0 - всё совпало.
/// <c>FfiSmoke --lib &lt;путь к libtc_ffi&gt; --golden &lt;каталог golden&gt; [--expect-panic-test]</c>
/// </summary>
internal static class Program
{
    private static int _checks;
    private static int _failures;

    private static int Main(string[] args)
    {
        string? lib = null;
        string? golden = null;
        bool expectPanic = false;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--lib" when i + 1 < args.Length:
                    lib = args[++i];
                    break;
                case "--golden" when i + 1 < args.Length:
                    golden = args[++i];
                    break;
                case "--expect-panic-test":
                    expectPanic = true;
                    break;
                default:
                    return Usage();
            }
        }

        if (lib == null || golden == null)
        {
            return Usage();
        }

        string libPath = Path.GetFullPath(lib);
        IntPtr libHandle = NativeLibrary.Load(libPath);
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) => name == Native.Lib ? libHandle : IntPtr.Zero);
        bool hasPanicExport = NativeLibrary.TryGetExport(libHandle, "tc_sim_test_panic", out _);

        string goldenDir = Path.GetFullPath(golden);
        string root = Path.GetFullPath(Path.Combine(goldenDir, "..", "..", ".."));

        CheckAbi();
        int scenarios = 0;
        foreach (string file in Directory.GetFiles(goldenDir, "*.json"))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (!doc.RootElement.TryGetProperty("scenario", out JsonElement sc))
            {
                continue;
            }

            CheckScenario(root, Path.GetFileName(file), sc.GetString()!, doc.RootElement);
            scenarios++;
        }

        Check(scenarios >= 10, $"сценарных golden: {scenarios} (ожидали >= 10)");
        CheckNullArguments(root);
        CheckQueue(root);
        if (hasPanicExport)
        {
            CheckPanic(root);
        }
        else
        {
            Check(!expectPanic, "--expect-panic-test: в библиотеке нет tc_sim_test_panic (собрать с --features test-panic)");
            Console.WriteLine("ffi-smoke: проверка паники пропущена (библиотека без фичи test-panic)");
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"ffi-smoke: {_checks} проверок, {_failures} ошибок, сценариев {scenarios}"));
        return _failures == 0 ? 0 : 1;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("usage: FfiSmoke --lib <libtc_ffi.so|dll|dylib> --golden <каталог tests/golden/rust-pilot> [--expect-panic-test]");
        return 2;
    }

    private static void Check(bool ok, string what)
    {
        _checks++;
        if (!ok)
        {
            _failures++;
            Console.Error.WriteLine("FAIL: " + what);
        }
    }

    private static void CheckEq<T>(T expected, T actual, string what)
        where T : IEquatable<T>
        => Check(expected.Equals(actual), $"{what}: ожидали {expected}, получили {actual}");

    private static void CheckAbi()
    {
        CheckEq(1u, Native.tc_abi_version(), "tc_abi_version");
        CheckEq(Marshal.SizeOf<TcScenarioDesc>(), Native.tc_struct_size(Native.SzScenarioDesc), "размер tc_scenario_desc");
        CheckEq(Marshal.SizeOf<TcNodeDesc>(), Native.tc_struct_size(Native.SzNodeDesc), "размер tc_node_desc");
        CheckEq(Marshal.SizeOf<TcSegmentDesc>(), Native.tc_struct_size(Native.SzSegmentDesc), "размер tc_segment_desc");
        CheckEq(Marshal.SizeOf<TcRuntimeOptions>(), Native.tc_struct_size(Native.SzRuntimeOptions), "размер tc_runtime_options");
        CheckEq(Marshal.SizeOf<TcCommand>(), Native.tc_struct_size(Native.SzCommand), "размер tc_command (зеркало)");
        CheckEq(Marshal.SizeOf<TcVehicleRenderData>(), Native.tc_struct_size(Native.SzVehicleRenderData), "размер tc_vehicle_render_data (зеркало)");
        CheckEq(Marshal.SizeOf<TcVec2>(), Native.tc_struct_size(Native.SzVec2), "размер tc_vec2");
        CheckEq(Marshal.SizeOf<TcPilotCounters>(), Native.tc_struct_size(Native.SzPilotCounters), "размер tc_pilot_counters");
        CheckEq(Marshal.SizeOf<TcCapacities>(), Native.tc_struct_size(Native.SzCapacities), "размер tc_capacities");
        // Настоящие типы ядра C# (не зеркала): та же раскладка.
        CheckEq(Unsafe.SizeOf<Command>(), Native.tc_struct_size(Native.SzCommand), "размер Command (Sim.Core)");
        CheckEq(Unsafe.SizeOf<VehicleRenderData>(), Native.tc_struct_size(Native.SzVehicleRenderData), "размер VehicleRenderData (Sim.Core)");
        CheckEq(-1, Native.tc_struct_size(9999), "tc_struct_size(неизвестный)");
    }

    private static TcCapacities Caps(in SimCapacities c) => new()
    {
        MaxVehicles = c.MaxVehicles,
        MaxNodes = c.MaxNodes,
        MaxSegments = c.MaxSegments,
        MaxLanes = c.MaxLanes,
        MaxLanePoints = c.MaxLanePoints,
        MaxZones = c.MaxZones,
        MaxBusRoutes = c.MaxBusRoutes,
        MaxBusStops = c.MaxBusStops,
        MaxBusStopsPerRoute = c.MaxBusStopsPerRoute,
        MaxRoutes = c.MaxRoutes,
        MaxPlanSteps = c.MaxPlanSteps,
        MaxPendingTrips = c.MaxPendingTrips,
        CommandQueueCapacity = c.CommandQueueCapacity,
        EventCapacity = c.EventCapacity,
        MaxSignalizedNodes = c.MaxSignalizedNodes,
        MaxConnectors = c.MaxConnectors,
        MaxConflicts = c.MaxConflicts,
        MaxPeds = c.MaxPeds,
        MaxResidents = c.MaxResidents,
        MaxPedPathSteps = c.MaxPedPathSteps,
        MaxLots = c.MaxLots,
        MaxBuildings = c.MaxBuildings,
        MaxCityResidents = c.MaxCityResidents,
        SkimObsCapacity = c.SkimObsCapacity,
    };

    /// <summary>Создаёт Rust-экземпляр из C#-desc; возвращает код <c>tc_sim_create</c>.</summary>
    private static unsafe int CreateRust(in ScenarioDesc desc, out SimHandle sim, int structSizeDelta = 0)
    {
        NodeDesc[] ns = desc.Network.Nodes ?? [];
        SegmentDesc[] ss = desc.Network.Segments ?? [];
        var nodes = new TcNodeDesc[ns.Length];
        for (int i = 0; i < ns.Length; i++)
        {
            nodes[i] = new TcNodeDesc { X = ns[i].X, Z = ns[i].Z, Control = (byte)ns[i].Control };
        }

        var segs = new TcSegmentDesc[ss.Length];
        for (int i = 0; i < ss.Length; i++)
        {
            segs[i] = new TcSegmentDesc { A = ss[i].A, B = ss[i].B, LanesAB = ss[i].LanesAB, LanesBA = ss[i].LanesBA, SpeedLimit = ss[i].SpeedLimit, Kind = (byte)ss[i].Kind };
        }

        fixed (TcNodeDesc* pn = nodes)
        fixed (TcSegmentDesc* ps = segs)
        {
            var d = new TcScenarioDesc
            {
                StructSize = Marshal.SizeOf<TcScenarioDesc>() + structSizeDelta,
                Seed = desc.Seed,
                Capacities = Caps(in desc.Capacities),
                Nodes = nodes.Length == 0 ? null : pn,
                NodeCount = nodes.Length,
                Segments = segs.Length == 0 ? null : ps,
                SegmentCount = segs.Length,
            };
            var o = new TcRuntimeOptions { StructSize = Marshal.SizeOf<TcRuntimeOptions>(), WorkerThreads = 3 };
            return Native.tc_sim_create(&d, &o, out sim);
        }
    }

    private static bool TryLoad(string root, string scenario, out ScenarioDesc desc)
    {
        try
        {
            desc = ScenarioLoader.Load(Path.Combine(root, scenario));
            return true;
        }
        catch (ScenarioException)
        {
            desc = default;
            return false;
        }
    }

    private static unsafe void CheckScenario(string root, string name, string scenario, JsonElement g)
    {
        bool loadOk = g.GetProperty("load_ok").GetBoolean();
        bool loaded = TryLoad(root, scenario, out ScenarioDesc desc);
        CheckEq(loadOk, loaded, $"{name}: load_ok");
        if (!loaded || !loadOk)
        {
            return;
        }

        int goldenStatus = g.GetProperty("create_status").GetInt32();
        CreateStatus csStatus = Simulation.TryCreate(in desc, out Simulation? csSim);
        using (csSim)
        {
            CheckEq(goldenStatus, (int)csStatus, $"{name}: create_status C# против golden");
            int rc = CreateRust(in desc, out SimHandle rust);
            using (rust)
            {
                CheckEq(goldenStatus, rc, $"{name}: create_status Rust против golden");
                if (rc != 0 || csSim == null)
                {
                    Check(rust.IsInvalid, $"{name}: при ошибке create handle должен быть NULL");
                    return;
                }

                ulong cs = csSim.ComputeNetworkHash();
                ulong rs = Native.tc_sim_network_hash(rust);
                CheckEq(cs, rs, $"{name}: network_hash C# против Rust");
                CheckEq(g.GetProperty("network_hash").GetString()!, rs.ToString("x16", CultureInfo.InvariantCulture), $"{name}: network_hash против golden");

                TcVec2* pts = Native.tc_sim_lane_points(rust, out int nPts);
                CheckEq(g.GetProperty("counts").GetProperty("lane_points").GetInt32(), nPts, $"{name}: число точек полос");
                Check(pts != null && nPts > 0, $"{name}: lane_points не NULL");

                TcVehicleRenderData* veh = Native.tc_sim_vehicles(rust, out int nVeh);
                CheckEq(desc.Capacities.MaxVehicles, nVeh, $"{name}: длина снапшота ТС");
                bool zero = veh != null;
                for (int i = 0; zero && i < nVeh; i++)
                {
                    zero = veh[i].X == 0f && veh[i].Flags == 0 && veh[i].Index == 0;
                }

                Check(zero, $"{name}: снапшот ТС из нулей");

                CheckEq(0, Native.tc_sim_step(rust, 100), $"{name}: step(100)");
                CheckEq(100L, Native.tc_sim_tick(rust), $"{name}: tick после step(100)");
                CheckEq(rs, Native.tc_sim_network_hash(rust), $"{name}: network_hash не меняется после step");
            }
        }
    }

    private static unsafe void CheckNullArguments(string root)
    {
        CheckEq(Native.ErrNull, Native.tc_sim_create_raw(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero), "create(NULL, NULL, NULL)");
        CheckEq(Native.ErrNull, Native.tc_sim_step_raw(IntPtr.Zero, 1), "step(NULL)");
        CheckEq(Native.ErrNull, Native.tc_sim_enqueue_raw(IntPtr.Zero, IntPtr.Zero), "enqueue(NULL, NULL)");
        CheckEq(-1L, Native.tc_sim_tick_raw(IntPtr.Zero), "tick(NULL)");
        CheckEq(0UL, Native.tc_sim_network_hash_raw(IntPtr.Zero), "network_hash(NULL)");
        Native.tc_sim_destroy(IntPtr.Zero); // no-op, не падает
        Check(true, "destroy(NULL)");

        if (!TryLoad(root, "scenarios/demo-grid.json", out ScenarioDesc desc))
        {
            Check(false, "demo-grid не загрузился");
            return;
        }

        CheckEq(Native.ErrAbi, CreateRust(in desc, out SimHandle bad, structSizeDelta: 4), "create с неверным struct_size");
        Check(bad.IsInvalid, "create с неверным struct_size: handle NULL");
        bad.Dispose();

        using SimHandle ok = MakeSim(in desc);
        CheckEq(Native.ErrArg, Native.tc_sim_step(ok, 0), "step(0)");
        CheckEq(Native.ErrArg, Native.tc_sim_step(ok, -5), "step(-5)");
        CheckEq(Native.ErrNull, Native.tc_sim_enqueue(ok, null), "enqueue(sim, NULL)");

        // desc без массивов при ненулевом счётчике.
        var d = new TcScenarioDesc { StructSize = Marshal.SizeOf<TcScenarioDesc>(), NodeCount = 3, Capacities = Caps(in desc.Capacities) };
        CheckEq(Native.ErrNull, Native.tc_sim_create(&d, null, out SimHandle h2), "create: nodes == NULL при node_count > 0");
        h2.Dispose();
    }

    private static SimHandle MakeSim(in ScenarioDesc desc)
    {
        int rc = CreateRust(in desc, out SimHandle sim);
        CheckEq(0, rc, "create demo-grid");
        return sim;
    }

    private static unsafe void CheckQueue(string root)
    {
        if (!TryLoad(root, "scenarios/demo-grid.json", out ScenarioDesc desc))
        {
            Check(false, "demo-grid не загрузился");
            return;
        }

        // Ёмкость очереди ограничиваем: переполнение за разумное число вызовов.
        desc.Capacities.CommandQueueCapacity = 8;
        using SimHandle sim = MakeSim(in desc);
        var cmd = new TcCommand { Kind = (int)CommandKind.SetTargetVehicleCount, Tag = 1, PayloadInt = 500 };
        for (int i = 0; i < 8; i++)
        {
            CheckEq(1, Native.tc_sim_enqueue(sim, &cmd), $"enqueue #{i}");
        }

        CheckEq(0, Native.tc_sim_enqueue(sim, &cmd), "enqueue в полную очередь -> 0");
        Native.tc_sim_pilot_counters(sim, out TcPilotCounters c);
        CheckEq(0L, c.CommandsApplied, "applied до step");
        CheckEq(1L, c.CommandsDropped, "dropped после переполнения");
        CheckEq(0, Native.tc_sim_step(sim, 1), "step(1) после очереди");
        Native.tc_sim_pilot_counters(sim, out c);
        CheckEq(8L, c.CommandsApplied, "applied после step");
        CheckEq(1L, c.CommandsDropped, "dropped не меняется");
        CheckEq(1, Native.tc_sim_enqueue(sim, &cmd), "enqueue после освобождения очереди");
    }

    private static void CheckPanic(string root)
    {
        if (!TryLoad(root, "scenarios/demo-grid.json", out ScenarioDesc desc))
        {
            Check(false, "demo-grid не загрузился");
            return;
        }

        using SimHandle sim = MakeSim(in desc);
        CheckEq(Native.ErrPanic, Native.tc_sim_test_panic(sim), "test_panic -> TC_ERR_PANIC");
        CheckEq(Native.ErrPoisoned, Native.tc_sim_step(sim, 1), "step после паники -> TC_ERR_POISONED");
        CheckEq(Native.ErrPoisoned, Native.tc_sim_test_panic(sim), "повторный вызов -> TC_ERR_POISONED");
        CheckEq(0UL, Native.tc_sim_network_hash(sim), "network_hash отравленного = 0");
        // using освободит handle: destroy на отравленном экземпляре работает (процесс не падает).
    }
}
