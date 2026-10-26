//! C ABI Rust-пилота (rust-pilot.md §2, ADR-0022). Заголовок - `include/tc_sim.h`, пишется руками, держать в синхроне.
//!
//! Единственный крейт с `unsafe` (`tc-core` - `forbid(unsafe_code)`). Каждая `extern "C"` - тонкая обёртка:
//! проверка NULL -> `catch_unwind` -> код. Паника помечает экземпляр «отравленным» (`TC_ERR_POISONED`), `destroy` работает.
//! Память: вход - вызывающего (читаем внутри вызова, всё копируем); выход (`ptr + len`) - ядра, только чтение, валиден
//! до следующего `tc_sim_step`/`tc_sim_destroy`. Аллокатор границу не пересекает.
#![deny(unsafe_op_in_unsafe_fn)]

use std::panic::{AssertUnwindSafe, catch_unwind};
use std::ptr;
use std::sync::atomic::{AtomicBool, Ordering};

use tc_core::api::command::{COMMAND_SIZE, Command, PAYLOAD_SIZE, PilotCounters};
use tc_core::api::desc::{NetworkDesc, NodeControl, NodeDesc, RoadKind, ScenarioDesc, SegmentDesc, SimCapacities};
use tc_core::api::snapshot::VehicleRenderData;
use tc_core::api::status::CreateStatus;
use tc_core::sim::Sim;
use tc_core::util::Vec2;

pub const TC_ABI_VERSION: u32 = 1;

pub const TC_ERR_NULL: i32 = -1;
pub const TC_ERR_ARG: i32 = -2;
pub const TC_ERR_PANIC: i32 = -3;
pub const TC_ERR_POISONED: i32 = -4;
pub const TC_ERR_ABI: i32 = -5;

/// Идентификаторы для `tc_struct_size`.
pub const TC_SZ_SCENARIO_DESC: i32 = 0;
pub const TC_SZ_NODE_DESC: i32 = 1;
pub const TC_SZ_SEGMENT_DESC: i32 = 2;
pub const TC_SZ_RUNTIME_OPTIONS: i32 = 3;
pub const TC_SZ_COMMAND: i32 = 4;
pub const TC_SZ_VEHICLE_RENDER_DATA: i32 = 5;
pub const TC_SZ_VEC2: i32 = 6;
pub const TC_SZ_PILOT_COUNTERS: i32 = 7;
pub const TC_SZ_CAPACITIES: i32 = 8;

/// `SimCapacities`: 24 x `int32` в порядке объявления.
#[derive(Clone, Copy)]
#[repr(C)]
pub struct TcCapacities {
    pub max_vehicles: i32,
    pub max_nodes: i32,
    pub max_segments: i32,
    pub max_lanes: i32,
    pub max_lane_points: i32,
    pub max_zones: i32,
    pub max_bus_routes: i32,
    pub max_bus_stops: i32,
    pub max_bus_stops_per_route: i32,
    pub max_routes: i32,
    pub max_plan_steps: i32,
    pub max_pending_trips: i32,
    pub command_queue_capacity: i32,
    pub event_capacity: i32,
    pub max_signalized_nodes: i32,
    pub max_connectors: i32,
    pub max_conflicts: i32,
    pub max_peds: i32,
    pub max_residents: i32,
    pub max_ped_path_steps: i32,
    pub max_lots: i32,
    pub max_buildings: i32,
    pub max_city_residents: i32,
    pub skim_obs_capacity: i32,
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct TcNodeDesc {
    pub x: f32,
    pub z: f32,
    pub control: u8,
    pub _pad: [u8; 3],
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct TcSegmentDesc {
    pub a: i32,
    pub b: i32,
    pub lanes_ab: i32,
    pub lanes_ba: i32,
    pub speed_limit: f32,
    pub kind: u8,
    pub _pad: [u8; 3],
}

/// Параметры построителя сети (`NetworkOptions`) зашиты в обеих реализациях (`NetworkOptions.Default`) и через ABI не передаются.
#[repr(C)]
pub struct TcScenarioDesc {
    pub struct_size: i32,
    pub _pad0: i32,
    pub seed: u64,
    pub capacities: TcCapacities,
    pub nodes: *const TcNodeDesc,
    pub node_count: i32,
    pub _pad1: i32,
    pub segments: *const TcSegmentDesc,
    pub segment_count: i32,
    pub _pad2: i32,
}

#[derive(Clone, Copy)]
#[repr(C)]
pub struct TcRuntimeOptions {
    pub struct_size: i32,
    pub worker_threads: i32,
}

/// Раскладка 1:1 с `Command` (64 Б): `kind`, `tag`, 56 байт полезной нагрузки.
pub type TcCommand = Command;
pub type TcVehicleRenderData = VehicleRenderData;
pub type TcVec2 = Vec2;
pub type TcPilotCounters = PilotCounters;

const _: () = assert!(size_of::<TcCapacities>() == 96);
const _: () = assert!(size_of::<TcNodeDesc>() == 12);
const _: () = assert!(size_of::<TcSegmentDesc>() == 24);
const _: () = assert!(size_of::<TcRuntimeOptions>() == 8);
const _: () = assert!(size_of::<TcVec2>() == 8);
const _: () = assert!(size_of::<TcCommand>() == COMMAND_SIZE);
const _: () = assert!(PAYLOAD_SIZE == 56);
#[cfg(target_pointer_width = "64")]
const _: () = assert!(size_of::<TcScenarioDesc>() == 144);

/// Непрозрачный экземпляр (`tc_sim`).
pub struct TcSim {
    sim: Sim,
    poisoned: AtomicBool,
}

fn node_control(v: u8) -> Option<NodeControl> {
    match v {
        0 => Some(NodeControl::None),
        1 => Some(NodeControl::Yield),
        2 => Some(NodeControl::Stop),
        3 => Some(NodeControl::Signal),
        4 => Some(NodeControl::RightHandRule),
        _ => None,
    }
}

fn road_kind(v: u8) -> Option<RoadKind> {
    match v {
        0 => Some(RoadKind::Street),
        1 => Some(RoadKind::Arterial),
        _ => None,
    }
}

fn capacities(c: &TcCapacities) -> SimCapacities {
    SimCapacities {
        max_vehicles: c.max_vehicles,
        max_nodes: c.max_nodes,
        max_segments: c.max_segments,
        max_lanes: c.max_lanes,
        max_lane_points: c.max_lane_points,
        max_zones: c.max_zones,
        max_bus_routes: c.max_bus_routes,
        max_bus_stops: c.max_bus_stops,
        max_bus_stops_per_route: c.max_bus_stops_per_route,
        max_routes: c.max_routes,
        max_plan_steps: c.max_plan_steps,
        max_pending_trips: c.max_pending_trips,
        command_queue_capacity: c.command_queue_capacity,
        event_capacity: c.event_capacity,
        max_signalized_nodes: c.max_signalized_nodes,
        max_connectors: c.max_connectors,
        max_conflicts: c.max_conflicts,
        max_peds: c.max_peds,
        max_residents: c.max_residents,
        max_ped_path_steps: c.max_ped_path_steps,
        max_lots: c.max_lots,
        max_buildings: c.max_buildings,
        max_city_residents: c.max_city_residents,
        skim_obs_capacity: c.skim_obs_capacity,
    }
}

/// Копирует desc вызывающего во владение ядра. `Err` - готовый код возврата (`TC_ERR_*` или `CreateStatus`).
///
/// # Safety
/// `d.nodes`/`d.segments` указывают на `node_count`/`segment_count` валидных элементов (или счётчик 0).
unsafe fn read_desc(d: &TcScenarioDesc) -> Result<ScenarioDesc, i32> {
    if d.node_count < 0 || d.segment_count < 0 {
        return Err(TC_ERR_ARG);
    }
    if (d.node_count > 0 && d.nodes.is_null()) || (d.segment_count > 0 && d.segments.is_null()) {
        return Err(TC_ERR_NULL);
    }
    let nodes: &[TcNodeDesc] = if d.node_count == 0 {
        &[]
    } else {
        // SAFETY: контракт функции; указатель не NULL, счётчик неотрицателен.
        unsafe { std::slice::from_raw_parts(d.nodes, d.node_count as usize) }
    };
    let segments: &[TcSegmentDesc] = if d.segment_count == 0 {
        &[]
    } else {
        // SAFETY: контракт функции; указатель не NULL, счётчик неотрицателен.
        unsafe { std::slice::from_raw_parts(d.segments, d.segment_count as usize) }
    };
    let bad_network = CreateStatus::InvalidNetwork as i32;
    let mut out = NetworkDesc {
        nodes: Vec::with_capacity(nodes.len()),
        segments: Vec::with_capacity(segments.len()),
    };
    for n in nodes {
        let control = node_control(n.control).ok_or(bad_network)?;
        out.nodes.push(NodeDesc { x: n.x, z: n.z, control });
    }
    for s in segments {
        let kind = road_kind(s.kind).ok_or(bad_network)?;
        out.segments.push(SegmentDesc {
            a: s.a,
            b: s.b,
            lanes_ab: s.lanes_ab,
            lanes_ba: s.lanes_ba,
            speed_limit: s.speed_limit,
            kind,
        });
    }
    Ok(ScenarioDesc {
        seed: d.seed,
        capacities: capacities(&d.capacities),
        network: out,
    })
}

/// Общая обёртка вызова над экземпляром: NULL -> `TC_ERR_NULL`, отравлен -> `TC_ERR_POISONED`, паника -> `TC_ERR_PANIC` + отравление.
///
/// # Safety
/// `p` - NULL либо указатель из `tc_sim_create`, не освобождённый `tc_sim_destroy`, без одновременных вызовов.
unsafe fn guard<R>(p: *const TcSim, null: R, poisoned: R, panicked: R, f: impl FnOnce(&TcSim) -> R) -> R {
    if p.is_null() {
        return null;
    }
    // SAFETY: контракт функции.
    let s = unsafe { &*p };
    if s.poisoned.load(Ordering::Relaxed) {
        return poisoned;
    }
    match catch_unwind(AssertUnwindSafe(|| f(s))) {
        Ok(r) => r,
        Err(_) => {
            s.poisoned.store(true, Ordering::Relaxed);
            panicked
        }
    }
}

/// То же для вызовов, меняющих экземпляр (`&mut`).
///
/// # Safety
/// `p` - NULL либо указатель из `tc_sim_create`, не освобождённый `tc_sim_destroy`; вызовы одного экземпляра не пересекаются (core-api §3).
unsafe fn guard_mut<R>(p: *mut TcSim, null: R, poisoned: R, panicked: R, f: impl FnOnce(&mut TcSim) -> R) -> R {
    if p.is_null() {
        return null;
    }
    // SAFETY: контракт функции (эксклюзивный доступ).
    let s = unsafe { &mut *p };
    if s.poisoned.load(Ordering::Relaxed) {
        return poisoned;
    }
    match catch_unwind(AssertUnwindSafe(|| f(&mut *s))) {
        Ok(r) => r,
        Err(_) => {
            s.poisoned.store(true, Ordering::Relaxed);
            panicked
        }
    }
}

/// Версия ABI (`TC_ABI_VERSION`).
#[unsafe(no_mangle)]
pub extern "C" fn tc_abi_version() -> u32 {
    TC_ABI_VERSION
}

/// Размеры struct ABI (`TC_SZ_*`) для сверки с хостом; -1 = неизвестный идентификатор.
#[unsafe(no_mangle)]
pub extern "C" fn tc_struct_size(which: i32) -> i32 {
    let n = match which {
        TC_SZ_SCENARIO_DESC => size_of::<TcScenarioDesc>(),
        TC_SZ_NODE_DESC => size_of::<TcNodeDesc>(),
        TC_SZ_SEGMENT_DESC => size_of::<TcSegmentDesc>(),
        TC_SZ_RUNTIME_OPTIONS => size_of::<TcRuntimeOptions>(),
        TC_SZ_COMMAND => size_of::<TcCommand>(),
        TC_SZ_VEHICLE_RENDER_DATA => size_of::<TcVehicleRenderData>(),
        TC_SZ_VEC2 => size_of::<TcVec2>(),
        TC_SZ_PILOT_COUNTERS => size_of::<TcPilotCounters>(),
        TC_SZ_CAPACITIES => size_of::<TcCapacities>(),
        _ => return -1,
    };
    n as i32
}

/// Создаёт экземпляр. Возврат: 0 = `CreateStatus::Ok` (`*out` заполнен), > 0 = `CreateStatus` ошибки, < 0 = `TC_ERR_*`.
/// При любой ошибке `*out = NULL`. `opts` может быть NULL (значения по умолчанию).
///
/// # Safety
/// `desc` и `opts` - NULL либо указатели на валидные struct, чьи массивы живут до возврата; `out` - NULL либо запись одного указателя.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_create(desc: *const TcScenarioDesc, opts: *const TcRuntimeOptions, out: *mut *mut TcSim) -> i32 {
    if out.is_null() {
        return TC_ERR_NULL;
    }
    // SAFETY: `out` не NULL, контракт функции.
    unsafe { *out = ptr::null_mut() };
    if desc.is_null() {
        return TC_ERR_NULL;
    }
    // SAFETY: контракт функции.
    let d = unsafe { &*desc };
    if d.struct_size != size_of::<TcScenarioDesc>() as i32 {
        return TC_ERR_ABI;
    }
    if !opts.is_null() {
        // SAFETY: контракт функции.
        let o = unsafe { &*opts };
        if o.struct_size != size_of::<TcRuntimeOptions>() as i32 {
            return TC_ERR_ABI;
        }
        // worker_threads в пилоте принимается и игнорируется.
    }
    let result = catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: контракт функции (массивы desc валидны на время вызова).
        let sd = unsafe { read_desc(d) }?;
        Sim::create(&sd).map_err(|st| st as i32)
    }));
    match result {
        Ok(Ok(sim)) => {
            let b = Box::new(TcSim {
                sim,
                poisoned: AtomicBool::new(false),
            });
            // SAFETY: `out` не NULL, контракт функции.
            unsafe { *out = Box::into_raw(b) };
            CreateStatus::Ok as i32
        }
        Ok(Err(code)) => code,
        Err(_) => TC_ERR_PANIC,
    }
}

/// Освобождает экземпляр. NULL - no-op. Повторный вызов на том же указателе - UB. Работает и на отравленном экземпляре.
///
/// # Safety
/// `sim` - NULL либо указатель из `tc_sim_create`, ещё не освобождённый.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_destroy(sim: *mut TcSim) {
    if sim.is_null() {
        return;
    }
    // SAFETY: контракт функции; `Box::from_raw` парный к `Box::into_raw` в `tc_sim_create`.
    let b = unsafe { Box::from_raw(sim) };
    // Паника в Drop не должна пересекать границу.
    let _ = catch_unwind(AssertUnwindSafe(move || drop(b)));
}

/// `ticks` тиков. 0 = ok; `TC_ERR_ARG` при `ticks < 1`.
///
/// # Safety
/// См. `guard`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_step(sim: *mut TcSim, ticks: i32) -> i32 {
    // SAFETY: контракт функции.
    unsafe {
        guard_mut(sim, TC_ERR_NULL, TC_ERR_POISONED, TC_ERR_PANIC, |s| {
            if ticks < 1 {
                return TC_ERR_ARG;
            }
            s.sim.step(ticks);
            0
        })
    }
}

/// 1 = команда принята, 0 = очередь полна (счётчик `commands_dropped`++), < 0 = ошибка.
///
/// # Safety
/// См. `guard`; `cmd` - NULL либо указатель на 64 валидных байта.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_enqueue(sim: *mut TcSim, cmd: *const TcCommand) -> i32 {
    if cmd.is_null() {
        return TC_ERR_NULL;
    }
    // SAFETY: контракт функции; команда копируется.
    let c = unsafe { *cmd };
    // SAFETY: контракт функции.
    unsafe { guard_mut(sim, TC_ERR_NULL, TC_ERR_POISONED, TC_ERR_PANIC, |s| i32::from(s.sim.enqueue(c))) }
}

/// Текущий тик; -1 = NULL, -4 = отравлен.
///
/// # Safety
/// См. `guard`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_tick(sim: *const TcSim) -> i64 {
    // SAFETY: контракт функции.
    unsafe {
        guard(
            sim,
            i64::from(TC_ERR_NULL),
            i64::from(TC_ERR_POISONED),
            i64::from(TC_ERR_PANIC),
            |s| s.sim.tick(),
        )
    }
}

/// Снапшот ТС `[max_vehicles]` (в пилоте нули). `*out_len` пишется всегда (0 при ошибке, указатель NULL).
///
/// # Safety
/// См. `guard`; `out_len` - NULL либо запись одного `int32`. Указатель валиден до следующего `tc_sim_step`/`tc_sim_destroy`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_vehicles(sim: *const TcSim, out_len: *mut i32) -> *const TcVehicleRenderData {
    // SAFETY: контракт функции.
    unsafe { slice_out(sim, out_len, |s| s.sim.vehicles()) }
}

/// Точки полос `[lane_point_count]` - реальная геометрия. Контракт - как у `tc_sim_vehicles`.
///
/// # Safety
/// См. `tc_sim_vehicles`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_lane_points(sim: *const TcSim, out_len: *mut i32) -> *const TcVec2 {
    // SAFETY: контракт функции.
    unsafe { slice_out(sim, out_len, |s| s.sim.lane_points()) }
}

/// # Safety
/// См. `tc_sim_vehicles`.
unsafe fn slice_out<T>(sim: *const TcSim, out_len: *mut i32, f: impl FnOnce(&TcSim) -> &[T]) -> *const T {
    if out_len.is_null() {
        return ptr::null();
    }
    // SAFETY: `out_len` не NULL, контракт функции.
    unsafe { *out_len = 0 };
    // SAFETY: контракт функции.
    let (p, n) = unsafe {
        guard(sim, (ptr::null(), 0usize), (ptr::null(), 0usize), (ptr::null(), 0usize), |s| {
            let sl = f(s);
            (sl.as_ptr(), sl.len())
        })
    };
    if p.is_null() {
        return ptr::null();
    }
    // SAFETY: `out_len` не NULL; длина срезов пилота помещается в i32 (ёмкости <= 2^24).
    unsafe { *out_len = i32::try_from(n).unwrap_or(0) };
    p
}

/// `network_hash` (rust-pilot.md §5.2); 0 при NULL/отравлении/панике.
///
/// # Safety
/// См. `guard`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_network_hash(sim: *const TcSim) -> u64 {
    // SAFETY: контракт функции.
    unsafe { guard(sim, 0, 0, 0, |s| s.sim.network_hash()) }
}

/// Счётчики команд пилота; при ошибке нули. `out` NULL - no-op.
///
/// # Safety
/// См. `guard`; `out` - NULL либо запись одного `tc_pilot_counters`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_pilot_counters(sim: *const TcSim, out: *mut TcPilotCounters) {
    if out.is_null() {
        return;
    }
    // SAFETY: `out` не NULL, контракт функции.
    unsafe { *out = PilotCounters::default() };
    // SAFETY: контракт функции.
    let c = unsafe {
        guard(
            sim,
            PilotCounters::default(),
            PilotCounters::default(),
            PilotCounters::default(),
            |s| s.sim.counters(),
        )
    };
    // SAFETY: `out` не NULL, контракт функции.
    unsafe { *out = c };
}

/// Только с фичей `test-panic`: паника внутри обёртки (проверка `TC_ERR_PANIC`, затем `TC_ERR_POISONED`).
///
/// # Safety
/// См. `guard`.
#[cfg(feature = "test-panic")]
#[unsafe(no_mangle)]
pub unsafe extern "C" fn tc_sim_test_panic(sim: *mut TcSim) -> i32 {
    // SAFETY: контракт функции.
    unsafe {
        guard_mut(sim, TC_ERR_NULL, TC_ERR_POISONED, TC_ERR_PANIC, |_| {
            panic!("tc_sim_test_panic: намеренная паника")
        })
    }
}
