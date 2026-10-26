//! Порт `Rules/Intersections/ConnectorStore.cs`, часть R4: плотный массив connector'ов (регистрация: `Register`, `BuildAll`/`BuildConflicts` без пар).
//! Конфликтные пары (`ComputePairs`, `WriteRows`, `ComputeRel`, `ConflictEntry`) - R5; до него `conflicts_used` = 0, `first_conflict`/
//! `conflict_count`/`conflict_mask` нулевые.

use crate::network::builder::MAX_CONNECTORS_PER_NODE;
use crate::network::connectors::{ARM_MINOR_BIT, classify_turn};
use crate::network::store::NetworkStore;
use crate::network::types::{Maneuver, NetworkStatus, NodeData};

pub struct ConnectorStore {
    pub lane: Vec<i32>,
    pub node: Vec<i32>,
    pub in_lane: Vec<i32>,
    pub out_lane: Vec<i32>,
    /// Длина connector'а (производное, но входит в `network_hash`).
    pub length: Vec<f32>,
    /// Длина входной полосы connector'а.
    pub in_length: Vec<f32>,
    pub local_bit: Vec<u8>,
    pub maneuver: Vec<u8>,
    pub from_arm: Vec<u8>,
    /// Бит0 `FromMinorArm`.
    pub flags: Vec<u8>,
    pub first_conflict: Vec<i32>,
    pub conflict_count: Vec<i32>,
    pub conflict_mask: Vec<u64>,
    pub max_conflicts: usize,
    pub count: i32,
    pub conflicts_used: i32,
}

impl ConnectorStore {
    pub fn new(max_connectors: i32, max_conflicts: i32) -> Self {
        let c = max_connectors.max(0) as usize;
        Self {
            lane: vec![0; c],
            node: vec![0; c],
            in_lane: vec![0; c],
            out_lane: vec![0; c],
            length: vec![0.0; c],
            in_length: vec![0.0; c],
            local_bit: vec![0; c],
            maneuver: vec![0; c],
            from_arm: vec![0; c],
            flags: vec![0; c],
            first_conflict: vec![0; c],
            conflict_count: vec![0; c],
            conflict_mask: vec![0; c],
            max_conflicts: max_conflicts.max(0) as usize,
            count: 0,
            conflicts_used: 0,
        }
    }

    pub fn max_connectors(&self) -> usize {
        self.lane.len()
    }

    pub fn reset(&mut self) {
        self.count = 0;
        self.conflicts_used = 0;
    }

    /// Сброс и построение для всех узлов с connector'ами. Первая ошибка прерывает обход.
    pub fn build_all(&mut self, net: &mut NetworkStore) -> NetworkStatus {
        self.reset();
        for n in 0..net.node_count {
            if net.nodes[n as usize].connector_count == 0 {
                continue;
            }
            let st = self.build_conflicts(net, n);
            if st != NetworkStatus::Ok {
                return st;
            }
        }
        NetworkStatus::Ok
    }

    /// Регистрирует connector'ы узла (`conflict_group` полос). R4: пары конфликтов не строятся (R5 добавит шаг после регистрации
    /// для узлов с `control != None`). Повторный вызов для узла - `NodeAlreadyBuilt`.
    pub fn build_conflicts(&mut self, net: &mut NetworkStore, node: i32) -> NetworkStatus {
        if node < 0 || node >= net.node_count {
            return NetworkStatus::InvalidArgument;
        }
        let nd = net.nodes[node as usize];
        let k = nd.connector_count;
        if k == 0 {
            return NetworkStatus::Ok;
        }
        if k as usize > MAX_CONNECTORS_PER_NODE || nd.first_arm < 0 {
            return NetworkStatus::TooManyConnectors;
        }
        if net.lanes[nd.first_connector as usize].conflict_group >= 0 {
            return NetworkStatus::NodeAlreadyBuilt;
        }
        if self.count as usize + k as usize > self.lane.len() {
            return NetworkStatus::ConnectorsFull;
        }
        let g0 = self.count;
        self.register(net, node, g0);
        self.count += k;
        NetworkStatus::Ok
    }

    fn register(&mut self, net: &mut NetworkStore, node: i32, g0: i32) {
        let nd = net.nodes[node as usize];
        for i in 0..nd.connector_count {
            let lane = nd.first_connector + i;
            let g = (g0 + i) as usize;
            net.lanes[lane as usize].conflict_group = g0 + i;
            let ld = net.lanes[lane as usize];
            self.lane[g] = lane;
            self.node[g] = node;
            self.in_lane[g] = ld.from_lane;
            self.out_lane[g] = ld.to_lane;
            self.length[g] = ld.length;
            self.in_length[g] = net.lanes[ld.from_lane as usize].length;
            self.local_bit[g] = i as u8;
            let a = arm_of(net, &nd, net.lanes[ld.from_lane as usize].segment);
            let b = arm_of(net, &nd, net.lanes[ld.to_lane as usize].segment);
            self.from_arm[g] = a as u8;
            self.maneuver[g] = if a == b {
                Maneuver::UTurn as u8
            } else {
                classify_turn(
                    net.arm_dir[(nd.first_arm + a) as usize],
                    net.arm_dir[(nd.first_arm + b) as usize],
                    nd.arm_count == 2,
                ) as u8
            };
            self.flags[g] = u8::from((net.arm_flags[(nd.first_arm + a) as usize] & ARM_MINOR_BIT) != 0);
            self.first_conflict[g] = 0;
            self.conflict_count[g] = 0;
            self.conflict_mask[g] = 0;
        }
    }
}

fn arm_of(net: &NetworkStore, nd: &NodeData, segment: i32) -> i32 {
    for a in 0..nd.arm_count {
        if net.node_arms[(nd.first_arm + a) as usize] == segment {
            return a;
        }
    }
    0
}
