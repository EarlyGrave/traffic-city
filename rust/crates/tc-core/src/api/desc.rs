//! Описание сценария (подмножество `ScenarioDesc`: seed, ёмкости, явная сеть) и `desc_hash` (rust-pilot.md §5.2).
//!
//! Порт `Sim.Core/Api/ScenarioDesc` + `NetworkHash.Desc`. Порядок полей хэша - ровно как в C#.

use crate::util::Fnv1a64;

/// Версия формата хэшей (первое `Add(int)` каждого из четырёх хэшей).
pub const HASH_VERSION: i32 = 1;

/// Ёмкости ядра (`SimCapacities`); порядок полей - порядок объявления в C#, он же порядок в `desc_hash`.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct SimCapacities {
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

/// `NodeControl` (значения - как в C#).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(i32)]
pub enum NodeControl {
    None = 0,
    Yield = 1,
    Stop = 2,
    Signal = 3,
    RightHandRule = 4,
}

/// `RoadKind` (значения - как в C#).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum RoadKind {
    Street = 0,
    Arterial = 1,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NodeDesc {
    pub x: f32,
    pub z: f32,
    pub control: NodeControl,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SegmentDesc {
    pub a: i32,
    pub b: i32,
    pub lanes_ab: i32,
    pub lanes_ba: i32,
    /// м/с: `(float)(kmh / 3.6)`, деление в f64.
    pub speed_limit: f32,
    pub kind: RoadKind,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct NetworkDesc {
    pub nodes: Vec<NodeDesc>,
    pub segments: Vec<SegmentDesc>,
    /// `network.corner_radius_m` (ADR-0028): 0 - дефолт (6 м), < 0 - выключено (фиксированный отступ `node_setback`), иначе радиус.
    pub corner_radius: f32,
}

/// Подмножество `ScenarioDesc` пилота (`city.enabled = false`).
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ScenarioDesc {
    pub seed: u64,
    pub capacities: SimCapacities,
    pub network: NetworkDesc,
}

/// `NetworkOptions.Default` (C# `NetworkTypes.cs`): зашиты, из JSON читается только `corner_radius` (`network.corner_radius_m`, ADR-0028).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct NetworkOptions {
    pub lane_width: f32,
    pub snap_radius: f32,
    pub node_setback: f32,
    pub connector_samples: i32,
    pub min_lane_length: f32,
    /// Радиус скругления бордюра (ADR-0028): отступ рукава узла степени >= 3 = max(node_setback, полуширина поперечной + corner_radius); 0 - фиксированный `node_setback`.
    pub corner_radius: f32,
}

impl NetworkOptions {
    pub const DEFAULT: NetworkOptions = NetworkOptions {
        lane_width: 3.5,
        snap_radius: 5.0,
        node_setback: 7.0,
        connector_samples: 8,
        min_lane_length: 1.0,
        corner_radius: DEFAULT_CORNER_RADIUS,
    };

    /// Опция из поля сценария (`NetworkDesc::corner_radius`): 0 - дефолт, < 0 - выключено, иначе радиус (C# `CornerRadiusFromDesc`).
    pub fn corner_radius_from_desc(desc: f32) -> f32 {
        if desc == 0.0 {
            DEFAULT_CORNER_RADIUS
        } else if desc < 0.0 {
            0.0
        } else {
            desc
        }
    }
}

/// Радиус скругления по умолчанию (ADR-0028).
pub const DEFAULT_CORNER_RADIUS: f32 = 6.0;

impl Default for NetworkOptions {
    fn default() -> Self {
        Self::DEFAULT
    }
}

fn len_i32(n: usize) -> i32 {
    i32::try_from(n).unwrap_or(i32::MAX)
}

/// `desc_hash`: разобранный сценарий до `Create` (см. `NetworkHash.Desc`).
pub fn desc_hash(d: &ScenarioDesc) -> u64 {
    let mut h = Fnv1a64::new();
    h.add_i32(HASH_VERSION);
    h.add_u64(d.seed);
    let c = &d.capacities;
    for v in [
        c.max_vehicles,
        c.max_nodes,
        c.max_segments,
        c.max_lanes,
        c.max_lane_points,
        c.max_zones,
        c.max_bus_routes,
        c.max_bus_stops,
        c.max_bus_stops_per_route,
        c.max_routes,
        c.max_plan_steps,
        c.max_pending_trips,
        c.command_queue_capacity,
        c.event_capacity,
        c.max_signalized_nodes,
        c.max_connectors,
        c.max_conflicts,
        c.max_peds,
        c.max_residents,
        c.max_ped_path_steps,
        c.max_lots,
        c.max_buildings,
        c.max_city_residents,
        c.skim_obs_capacity,
    ] {
        h.add_i32(v);
    }

    h.add_i32(len_i32(d.network.nodes.len()));
    for n in &d.network.nodes {
        h.add_f32(n.x);
        h.add_f32(n.z);
        h.add_i32(n.control as i32);
    }

    h.add_i32(len_i32(d.network.segments.len()));
    for s in &d.network.segments {
        h.add_i32(s.a);
        h.add_i32(s.b);
        h.add_i32(s.lanes_ab);
        h.add_i32(s.lanes_ba);
        h.add_f32(s.speed_limit);
        h.add_u8(s.kind as u8);
    }

    let o = NetworkOptions::DEFAULT;
    h.add_f32(o.lane_width);
    h.add_f32(o.snap_radius);
    h.add_f32(o.node_setback);
    h.add_i32(o.connector_samples);
    h.add_f32(o.min_lane_length);
    h.add_f32(NetworkOptions::corner_radius_from_desc(d.network.corner_radius)); // ADR-0028: действующий радиус явной сети
    h.value()
}
