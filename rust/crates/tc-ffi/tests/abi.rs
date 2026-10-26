//! Тесты C ABI на стороне Rust (без хоста): размеры, коды ошибок, очередь команд, отравление после паники.
//! Полная сверка с C# - `rust/ffi-smoke`.

use std::ptr;

use tc_ffi::*;

fn caps() -> TcCapacities {
    TcCapacities {
        max_vehicles: 16,
        max_nodes: 16,
        max_segments: 32,
        max_lanes: 256,
        max_lane_points: 4096,
        max_zones: 0,
        max_bus_routes: 0,
        max_bus_stops: 0,
        max_bus_stops_per_route: 0,
        max_routes: 0,
        max_plan_steps: 0,
        max_pending_trips: 4,
        command_queue_capacity: 4,
        event_capacity: 16,
        max_signalized_nodes: 0,
        max_connectors: 0,
        max_conflicts: 0,
        max_peds: 0,
        max_residents: 0,
        max_ped_path_steps: 0,
        max_lots: 0,
        max_buildings: 0,
        max_city_residents: 0,
        skim_obs_capacity: 0,
    }
}

const NODES: [TcNodeDesc; 2] = [
    TcNodeDesc {
        x: 0.0,
        z: 0.0,
        control: 0,
        _pad: [0; 3],
    },
    TcNodeDesc {
        x: 200.0,
        z: 0.0,
        control: 0,
        _pad: [0; 3],
    },
];
const SEGMENTS: [TcSegmentDesc; 1] = [TcSegmentDesc {
    a: 0,
    b: 1,
    lanes_ab: 1,
    lanes_ba: 1,
    speed_limit: 13.9,
    kind: 0,
    _pad: [0; 3],
}];

fn desc() -> TcScenarioDesc {
    TcScenarioDesc {
        struct_size: size_of::<TcScenarioDesc>() as i32,
        _pad0: 0,
        seed: 1,
        capacities: caps(),
        nodes: NODES.as_ptr(),
        node_count: 2,
        _pad1: 0,
        segments: SEGMENTS.as_ptr(),
        segment_count: 1,
        _pad2: 0,
    }
}

fn create() -> *mut TcSim {
    let d = desc();
    let mut sim: *mut TcSim = ptr::null_mut();
    // SAFETY: d и массивы живут до конца вызова; out - валидный указатель.
    let rc = unsafe { tc_sim_create(&d, ptr::null(), &mut sim) };
    assert_eq!(rc, 0);
    assert!(!sim.is_null());
    sim
}

#[test]
fn struct_sizes() {
    assert_eq!(tc_abi_version(), 1);
    assert_eq!(tc_struct_size(TC_SZ_NODE_DESC), 12);
    assert_eq!(tc_struct_size(TC_SZ_SEGMENT_DESC), 24);
    assert_eq!(tc_struct_size(TC_SZ_COMMAND), 64);
    assert_eq!(tc_struct_size(TC_SZ_VEHICLE_RENDER_DATA), 32);
    assert_eq!(tc_struct_size(TC_SZ_VEC2), 8);
    assert_eq!(tc_struct_size(TC_SZ_RUNTIME_OPTIONS), 8);
    assert_eq!(tc_struct_size(TC_SZ_PILOT_COUNTERS), 16);
    assert_eq!(tc_struct_size(TC_SZ_CAPACITIES), 96);
    assert_eq!(tc_struct_size(TC_SZ_SCENARIO_DESC), 144);
    assert_eq!(tc_struct_size(99), -1);
}

#[test]
fn null_and_abi_errors() {
    let mut sim: *mut TcSim = ptr::null_mut();
    // SAFETY: проверка контракта на NULL/неверный размер; указатели валидны либо NULL.
    unsafe {
        assert_eq!(tc_sim_create(ptr::null(), ptr::null(), &mut sim), TC_ERR_NULL);
        assert!(sim.is_null());
        let mut d = desc();
        d.struct_size += 1;
        assert_eq!(tc_sim_create(&d, ptr::null(), &mut sim), TC_ERR_ABI);
        let d = desc();
        assert_eq!(tc_sim_create(&d, ptr::null(), ptr::null_mut()), TC_ERR_NULL);
        assert_eq!(tc_sim_step(ptr::null_mut(), 1), TC_ERR_NULL);
        assert_eq!(tc_sim_tick(ptr::null()), -1);
        assert_eq!(tc_sim_network_hash(ptr::null()), 0);
        let mut n = 7;
        assert!(tc_sim_vehicles(ptr::null(), &mut n).is_null());
        assert_eq!(n, 0);
        tc_sim_destroy(ptr::null_mut());
    }
}

#[test]
fn step_queue_and_snapshots() {
    let sim = create();
    // SAFETY: sim создан выше и освобождается один раз в конце.
    unsafe {
        assert_eq!(tc_sim_step(sim, 0), TC_ERR_ARG);
        assert_eq!(tc_sim_step(sim, 100), 0);
        assert_eq!(tc_sim_tick(sim), 100);

        let cmd = TcCommand {
            kind: 9,
            ..TcCommand::default()
        };
        assert_eq!(tc_sim_enqueue(sim, ptr::null()), TC_ERR_NULL);
        for _ in 0..4 {
            assert_eq!(tc_sim_enqueue(sim, &cmd), 1);
        }
        assert_eq!(tc_sim_enqueue(sim, &cmd), 0);
        let mut c = TcPilotCounters::default();
        tc_sim_pilot_counters(sim, &mut c);
        assert_eq!((c.commands_applied, c.commands_dropped), (0, 1));
        assert_eq!(tc_sim_step(sim, 1), 0);
        tc_sim_pilot_counters(sim, &mut c);
        assert_eq!((c.commands_applied, c.commands_dropped), (4, 1));

        let mut n = 0;
        let v = tc_sim_vehicles(sim, &mut n);
        assert!(!v.is_null());
        assert_eq!(n, 16);
        let mut m = 0;
        let p = tc_sim_lane_points(sim, &mut m);
        assert!(!p.is_null() && m > 0);
        assert_ne!(tc_sim_network_hash(sim), 0);
        tc_sim_destroy(sim);
    }
}

#[cfg(feature = "test-panic")]
#[test]
fn panic_poisons_instance() {
    let sim = create();
    // SAFETY: sim создан выше и освобождается один раз.
    unsafe {
        assert_eq!(tc_sim_test_panic(sim), TC_ERR_PANIC);
        assert_eq!(tc_sim_step(sim, 1), TC_ERR_POISONED);
        assert_eq!(tc_sim_network_hash(sim), 0);
        tc_sim_destroy(sim);
    }
}
