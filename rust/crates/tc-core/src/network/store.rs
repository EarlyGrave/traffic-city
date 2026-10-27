//! Порт `Network/NetworkStore.cs`: хранилище сети. Все массивы выделяются один раз по `SimCapacities`; после построения аллокаций нет.
//! Вне пилота (пропущено): зоны, автобусные остановки/маршруты, `NodeTicket`, `OutReserved`, `PedNet`, `RebuildLaneDerived`, `GetView`.

use crate::api::desc::{NodeControl, SimCapacities};
use crate::util::detmath::atan2_system;
use crate::util::{FixedHashMap, Vec2};

use super::profiles::RoadProfileStore;
use super::types::{LaneData, NodeData, SegmentData};

pub struct NetworkStore {
    pub nodes: Vec<NodeData>,
    pub segments: Vec<SegmentData>,
    pub lanes: Vec<LaneData>,
    /// Индексы сегментов, инцидентных узлу, по часовой стрелке.
    pub node_arms: Vec<i32>,
    /// Параллелен `node_arms`: бит0 `ArmMinor`.
    pub arm_flags: Vec<u8>,
    /// Параллелен `node_arms`: единичный вектор от узла вдоль рукава.
    pub arm_dir: Vec<Vec2>,
    /// Индексы connector'ов (диапазоны из `LaneData::first_next`).
    pub lane_next: Vec<i32>,
    pub segment_points: Vec<Vec2>,
    pub lane_points: Vec<Vec2>,
    pub lane_point_s: Vec<f32>,
    /// Курс отрезка k->k+1 полилинии (atan2); производные данные, в `network_hash` не входят (`headings_hash`).
    pub lane_piece_heading: Vec<f32>,
    /// Узел с воротами в конце Normal-полосы / узел gated connector'а, иначе -1; производное, но входит в `network_hash`.
    pub lane_gate_node: Vec<i32>,
    /// Горячая копия `LaneData::length`.
    pub lane_length: Vec<f32>,

    // Служебные индексы построения (в хэш не входят).
    pub(crate) inc_head: Vec<i32>,
    pub(crate) inc_next: Vec<i32>,
    pub(crate) cell_next: Vec<i32>,
    pub(crate) cells: FixedHashMap,

    pub node_count: i32,
    pub segment_count: i32,
    pub lane_count: i32,
    pub lane_point_count: i32,
    pub segment_point_count: i32,
    pub node_arm_count: i32,
    pub lane_next_count: i32,
    /// Каталог профилей дорог (ADR-0030); `None` - режим legacy. Задаётся один раз до построения сети, дальше только читается.
    pub profiles: Option<RoadProfileStore>,
    /// ++ при каждом изменении сети.
    pub version: i32,
}

impl NetworkStore {
    pub const MAX_ARMS: usize = 16;

    pub fn new(caps: &SimCapacities) -> Self {
        let nodes = caps.max_nodes.max(0) as usize;
        let segs = caps.max_segments.max(0) as usize;
        let lanes = caps.max_lanes.max(0) as usize;
        let pts = caps.max_lane_points.max(0) as usize;
        Self {
            nodes: vec![NodeData::EMPTY; nodes],
            segments: vec![SegmentData::EMPTY; segs],
            lanes: vec![LaneData::EMPTY; lanes],
            node_arms: vec![0; 2 * segs],
            arm_flags: vec![0; 2 * segs],
            arm_dir: vec![Vec2::new(0.0, 0.0); 2 * segs],
            lane_next: vec![0; 2 * lanes],
            segment_points: vec![Vec2::new(0.0, 0.0); 4 * segs],
            lane_points: vec![Vec2::new(0.0, 0.0); pts],
            lane_point_s: vec![0.0; pts],
            lane_piece_heading: vec![0.0; pts],
            lane_gate_node: vec![-1; lanes],
            lane_length: vec![0.0; lanes],
            inc_head: vec![0; nodes],
            inc_next: vec![0; 2 * segs],
            cell_next: vec![0; nodes],
            cells: FixedHashMap::new(nodes.max(1)),
            node_count: 0,
            segment_count: 0,
            lane_count: 0,
            lane_point_count: 0,
            segment_point_count: 0,
            node_arm_count: 0,
            lane_next_count: 0,
            profiles: None,
            version: 0,
        }
    }

    /// Курс отрезка a->b: тем же выражением, что `LaneGeometry` (нормировка + atan2).
    pub(crate) fn piece_heading(a: Vec2, b: Vec2) -> f32 {
        let d = b - a;
        let len = ((d.x * d.x) + (d.z * d.z)).sqrt();
        let dir = if len > 1e-9_f32 { d * (1.0_f32 / len) } else { Vec2::new(1.0, 0.0) };
        atan2_system(dir.z, dir.x)
    }

    /// Заполняет кэш курса отрезков полосы, чьи `m` точек лежат с индекса `first`.
    pub(crate) fn fill_piece_headings(&mut self, first: usize, m: usize) {
        for k in 0..m - 1 {
            self.lane_piece_heading[first + k] = Self::piece_heading(self.lane_points[first + k], self.lane_points[first + k + 1]);
        }
        self.lane_piece_heading[first + m - 1] = 0.0;
    }

    /// Пересчёт `lane_gate_node` для полос узла: его connector'ы и их входные полосы получают номер узла, если у узла
    /// есть ворота (`control != None`), иначе -1.
    pub(crate) fn refresh_lane_gate_node(&mut self, node: usize) {
        let nd = self.nodes[node];
        if nd.first_arm < 0 {
            return;
        }
        let gate = if nd.control != NodeControl::None { node as i32 } else { -1 };
        for p in 0..nd.connector_count {
            let c = (nd.first_connector + p) as usize;
            self.lane_gate_node[c] = gate;
            let from = self.lanes[c].from_lane as usize;
            self.lane_gate_node[from] = gate;
        }
    }
}
