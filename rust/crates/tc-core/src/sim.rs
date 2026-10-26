//! Пилотный фасад ядра (rust-pilot.md §2, R4): `create` для явной сети (порядок и коды `CreateStatus` как у `WorldBuilder.Create`),
//! `step` (в пилоте только счётчик тиков, 0 аллокаций), хэши и счётчики для golden.

use crate::api::command::{Command, KIND_SET_TARGET_VEHICLE_COUNT, PilotCounters};
use crate::api::desc::{NetworkOptions, ScenarioDesc, SimCapacities};
use crate::api::snapshot::VehicleRenderData;
use crate::api::status::CreateStatus;
use crate::intersections::ConnectorStore;
use crate::network::hash::{conflicts_hash, headings_hash, network_hash};
use crate::network::{DefaultConnectorRules, NetworkBuilder, NetworkStatus, NetworkStore};
use crate::util::{RingBuffer, Vec2};

/// `WorldBuilder.MaxCapacity`.
const MAX_CAPACITY: i32 = 1 << 24;

/// Счётчики структур сети (поле `counts` golden-файла).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct NetworkCounts {
    pub nodes: i32,
    pub segments: i32,
    pub lanes: i32,
    pub lane_points: i32,
    pub segment_points: i32,
    pub arms: i32,
    pub lane_next: i32,
    pub connectors: i32,
    pub conflicts: i32,
}

pub struct Sim {
    net: NetworkStore,
    connectors: ConnectorStore,
    tick: i64,
    queue: RingBuffer<Command>,
    vehicles: Vec<VehicleRenderData>,
    target_vehicles: i32,
    counters: PilotCounters,
}

fn in_range(v: i32, min: i32) -> bool {
    (min..=MAX_CAPACITY).contains(&v)
}

/// `WorldBuilder.ValidateCapacities` (подмножество полей пилота и общие обязательные).
fn validate_capacities(c: &SimCapacities) -> CreateStatus {
    if !in_range(c.max_vehicles, 1)
        || !in_range(c.max_nodes, 1)
        || !in_range(c.max_segments, 1)
        || !in_range(c.max_lanes, 1)
        || !in_range(c.max_lane_points, 1)
        || !in_range(c.max_pending_trips, 1)
        || !in_range(c.command_queue_capacity, 1)
        || !in_range(c.event_capacity, 1)
    {
        return CreateStatus::InvalidCapacity;
    }
    if !in_range(c.max_zones, 0)
        || !in_range(c.max_bus_routes, 0)
        || !in_range(c.max_bus_stops, 0)
        || !in_range(c.max_bus_stops_per_route, 0)
        || !in_range(c.max_routes, 0)
        || !in_range(c.max_signalized_nodes, 0)
    {
        return CreateStatus::InvalidCapacity;
    }
    if i64::from(c.max_bus_routes) * i64::from(c.max_bus_stops_per_route) > i64::from(MAX_CAPACITY) {
        return CreateStatus::InvalidCapacity;
    }
    CreateStatus::Ok
}

impl Sim {
    /// `Simulation.Create` для явной сети. Ошибка - `CreateStatus` (без паник).
    pub fn create(desc: &ScenarioDesc) -> Result<Sim, CreateStatus> {
        let c = &desc.capacities;
        let st = validate_capacities(c);
        if st != CreateStatus::Ok {
            return Err(st);
        }

        let mut net = NetworkStore::new(c);
        let mut builder = NetworkBuilder::new(NetworkOptions::DEFAULT, DefaultConnectorRules);

        // 0 = по умолчанию (intersections.md §4): max_nodes * 16 connector'ов, max_connectors * 6 записей конфликтов.
        let max_connectors = if c.max_connectors > 0 {
            c.max_connectors
        } else {
            c.max_nodes.checked_mul(16).ok_or(CreateStatus::InvalidCapacity)?
        };
        let max_conflicts = if c.max_conflicts > 0 {
            c.max_conflicts
        } else {
            max_connectors.checked_mul(6).ok_or(CreateStatus::InvalidCapacity)?
        };
        let mut connectors = ConnectorStore::new(max_connectors, max_conflicts);

        // BuildNetwork: узлы без прилипания друг к другу, сегменты, connector'ы всех узлов.
        for (i, n) in desc.network.nodes.iter().enumerate() {
            let idx = builder
                .add_node(&mut net, Vec2::new(n.x, n.z), n.control)
                .map_err(NetworkStatus::to_create_status)?;
            // Прилипание сдвинуло бы индексы узлов относительно desc: явная сеть обязана быть разреженной (> snap_radius).
            if idx as usize != i {
                return Err(CreateStatus::InvalidNetwork);
            }
        }
        for sg in &desc.network.segments {
            builder
                .add_segment(&mut net, sg.a, sg.b, &[], sg.lanes_ab, sg.lanes_ba, sg.speed_limit, sg.kind)
                .map_err(NetworkStatus::to_create_status)?;
        }
        let built = builder.build_all_connectors(&mut net);
        if built != NetworkStatus::Ok {
            return Err(built.to_create_status());
        }

        let st = connectors.build_all(&mut net).to_create_status();
        if st != CreateStatus::Ok {
            return Err(st);
        }

        Ok(Sim {
            net,
            connectors,
            tick: 0,
            queue: RingBuffer::new(c.command_queue_capacity as usize),
            vehicles: vec![VehicleRenderData::default(); c.max_vehicles as usize],
            target_vehicles: 0,
            counters: PilotCounters::default(),
        })
    }

    /// Пилотный шаг (rust-pilot.md §2): `ticks` раз - применить очередь команд (FIFO), `tick += 1`. 0 аллокаций.
    pub fn step(&mut self, ticks: i32) {
        for _ in 0..ticks.max(0) {
            while let Some(cmd) = self.queue.try_dequeue() {
                if cmd.kind == KIND_SET_TARGET_VEHICLE_COUNT {
                    self.target_vehicles = cmd.payload_i32();
                }
                self.counters.commands_applied += 1;
            }
            self.tick += 1;
        }
    }

    /// `true` - команда принята; `false` - очередь полна (`commands_dropped` растёт).
    pub fn enqueue(&mut self, cmd: Command) -> bool {
        if self.queue.try_enqueue(cmd) {
            true
        } else {
            self.counters.commands_dropped += 1;
            false
        }
    }

    pub fn counters(&self) -> PilotCounters {
        self.counters
    }

    /// Последнее принятое `SetTargetVehicleCount` (пилот только запоминает).
    pub fn target_vehicles(&self) -> i32 {
        self.target_vehicles
    }

    /// Снапшот ТС `[max_vehicles]`; в пилоте нули.
    pub fn vehicles(&self) -> &[VehicleRenderData] {
        &self.vehicles
    }

    /// Точки полос `[lane_point_count]` - реальная геометрия построенной сети.
    pub fn lane_points(&self) -> &[Vec2] {
        &self.net.lane_points[..self.net.lane_point_count as usize]
    }

    pub fn tick(&self) -> i64 {
        self.tick
    }

    pub fn network(&self) -> &NetworkStore {
        &self.net
    }

    pub fn connectors(&self) -> &ConnectorStore {
        &self.connectors
    }

    pub fn conflicts_hash(&self) -> u64 {
        conflicts_hash(&self.connectors)
    }

    pub fn network_hash(&self) -> u64 {
        network_hash(&self.net, &self.connectors)
    }

    pub fn headings_hash(&self) -> u64 {
        headings_hash(&self.net)
    }

    pub fn counts(&self) -> NetworkCounts {
        let n = &self.net;
        NetworkCounts {
            nodes: n.node_count,
            segments: n.segment_count,
            lanes: n.lane_count,
            lane_points: n.lane_point_count,
            segment_points: n.segment_point_count,
            arms: n.node_arm_count,
            lane_next: n.lane_next_count,
            connectors: self.connectors.count,
            conflicts: self.connectors.conflicts_used,
        }
    }
}
