//! Порт `Rules/Intersections/ConnectorStore.cs`: плотный массив connector'ов (R4: регистрация) и их конфликтные пары
//! (R5: `ComputePairs`, `WriteRows`, `ComputeRel`, `ConflictEntry`).

use crate::api::desc::NodeControl;
use crate::network::builder::MAX_CONNECTORS_PER_NODE;
use crate::network::connectors::{ARM_MINOR_BIT, STRAIGHT_COS, classify_turn};
use crate::network::store::NetworkStore;
use crate::network::types::{Maneuver, NetworkStatus, NodeData};
use crate::util::Vec2;
use crate::util::detmath::{max_net, min_net};

const MAX_LOCAL: usize = MAX_CONNECTORS_PER_NODE;
/// `ConnectorStore.ConflictWidth`: Cross, если полилинии ближе (м).
pub const CONFLICT_WIDTH: f32 = 2.0;

/// Вид конфликта двух connector'ов (intersections.md §3).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum ConflictKind {
    Cross = 0,
    Merge = 1,
}

/// Статическое отношение приоритета «я относительно другого» (intersections.md §3.1).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
pub enum ConflictRel {
    Yield = 0,
    Priority = 1,
    Tie = 2,
}

/// Запись конфликта на connector'е (8 байт в C#; `reserved` всегда 0).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct ConflictEntry {
    pub other_local: u8,
    /// Бит0..1: `ConflictKind`; бит2..3: `ConflictRel`.
    pub flags: u8,
    pub s_mine_cm: u16,
    pub s_other_cm: u16,
    pub reserved: u16,
}

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
    pub conflicts: Vec<ConflictEntry>,
    // Рабочие буферы `build_conflicts` (выделены один раз).
    kind: Vec<u8>,
    s_on: Vec<f32>,
    bbox: Vec<f32>,
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
            conflicts: vec![ConflictEntry::default(); max_conflicts.max(0) as usize],
            kind: vec![0; MAX_LOCAL * MAX_LOCAL],
            s_on: vec![0.0; MAX_LOCAL * MAX_LOCAL],
            bbox: vec![0.0; MAX_LOCAL * 4],
            count: 0,
            conflicts_used: 0,
        }
    }

    pub fn max_connectors(&self) -> usize {
        self.lane.len()
    }

    pub fn max_conflicts(&self) -> usize {
        self.conflicts.len()
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

    /// Регистрирует connector'ы узла (`conflict_group` полос) и строит конфликтные пары (§3), включая `Rel` (§3.1).
    /// Для узлов с `NodeControl::None` конфликтов нет. Повторный вызов для узла - `NodeAlreadyBuilt`.
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
        if nd.control == NodeControl::None {
            return NetworkStatus::Ok;
        }
        self.compute_pairs(net, g0 as usize, k as usize);
        self.write_rows(net, node, g0 as usize, k as usize)
    }

    /// Пересчёт `Rel` всех записей узла (смена `NodeControl`, §3.1); `minor_rules` - применять ли шаг 1.
    pub fn recompute_rel(&mut self, net: &NetworkStore, node: i32, minor_rules: bool) {
        let nd = net.nodes[node as usize];
        if nd.connector_count == 0 {
            return;
        }
        let g0 = net.lanes[nd.first_connector as usize].conflict_group;
        if g0 < 0 {
            return;
        }
        for i in 0..nd.connector_count {
            let g = (g0 + i) as usize;
            let first = self.first_conflict[g] as usize;
            let end = first + self.conflict_count[g] as usize;
            for e in first..end {
                let other = g0 + i32::from(self.conflicts[e].other_local);
                let rel = self.compute_rel(net, &nd, g, other as usize, minor_rules);
                let f = self.conflicts[e].flags;
                self.conflicts[e].flags = (f & 3) | ((rel as u8) << 2);
            }
        }
    }

    /// Статическое отношение приоритета connector'а `c` к `other` (глобальные индексы, §3.1).
    pub fn compute_rel(&self, net: &NetworkStore, nd: &NodeData, c: usize, other: usize, minor_rules: bool) -> ConflictRel {
        let a = i32::from(self.from_arm[c]);
        let b = i32::from(self.from_arm[other]);
        if minor_rules {
            let ma = (self.flags[c] & 1) != 0;
            let mb = (self.flags[other] & 1) != 0;
            if ma && !mb {
                return ConflictRel::Yield;
            }
            if !ma && mb {
                return ConflictRel::Priority;
            }
        }
        if a == b {
            return ConflictRel::Tie;
        }
        let in_a = net.arm_dir[(nd.first_arm + a) as usize] * -1.0;
        let dir_b = net.arm_dir[(nd.first_arm + b) as usize];
        let dot = (in_a.x * dir_b.x) + (in_a.z * dir_b.z);
        if dot >= STRAIGHT_COS {
            let mine = self.maneuver[c];
            let theirs = self.maneuver[other];
            let i_turn_left = mine == Maneuver::Left as u8 || mine == Maneuver::UTurn as u8;
            let they_turn_left = theirs == Maneuver::Left as u8 || theirs == Maneuver::UTurn as u8;
            if i_turn_left && !they_turn_left {
                return ConflictRel::Yield;
            }
            if !i_turn_left && they_turn_left {
                return ConflictRel::Priority;
            }
            return ConflictRel::Tie;
        }
        let cross = (in_a.x * dir_b.z) - (in_a.z * dir_b.x);
        if cross >= 0.0 { ConflictRel::Yield } else { ConflictRel::Priority }
    }

    /// Заполняет `kind`/`s_on` для всех пар i < j узла (симметрично).
    fn compute_pairs(&mut self, net: &NetworkStore, g0: usize, k: usize) {
        for i in 0..k {
            let ld = net.lanes[self.lane[g0 + i] as usize];
            let mut x0 = f32::MAX;
            let mut z0 = f32::MAX;
            let mut x1 = f32::MIN;
            let mut z1 = f32::MIN;
            for p in 0..ld.point_count {
                let v = net.lane_points[(ld.first_point + p) as usize];
                x0 = min_net(x0, v.x);
                z0 = min_net(z0, v.z);
                x1 = max_net(x1, v.x);
                z1 = max_net(z1, v.z);
            }
            self.bbox[i * 4] = x0 - CONFLICT_WIDTH;
            self.bbox[i * 4 + 1] = z0 - CONFLICT_WIDTH;
            self.bbox[i * 4 + 2] = x1 + CONFLICT_WIDTH;
            self.bbox[i * 4 + 3] = z1 + CONFLICT_WIDTH;
        }

        for i in 0..k {
            self.kind[i * MAX_LOCAL + i] = 0;
            for j in (i + 1)..k {
                let ij = i * MAX_LOCAL + j;
                let ji = j * MAX_LOCAL + i;
                self.kind[ij] = 0;
                self.kind[ji] = 0;
                let gi = g0 + i;
                let gj = g0 + j;
                if self.in_lane[gi] == self.in_lane[gj] {
                    continue; // diverge: конфликта нет
                }
                if self.out_lane[gi] == self.out_lane[gj] {
                    let merge = 1 + ConflictKind::Merge as u8;
                    self.kind[ij] = merge;
                    self.kind[ji] = merge;
                    self.s_on[ij] = net.lanes[self.lane[gi] as usize].length;
                    self.s_on[ji] = net.lanes[self.lane[gj] as usize].length;
                    continue;
                }
                let b = &self.bbox;
                if b[i * 4 + 2] < b[j * 4] || b[j * 4 + 2] < b[i * 4] || b[i * 4 + 3] < b[j * 4 + 1] || b[j * 4 + 3] < b[i * 4 + 1] {
                    continue;
                }
                let (best, si, sj) = closest_approach(net, self.lane[gi], self.lane[gj]);
                if best < CONFLICT_WIDTH * CONFLICT_WIDTH {
                    let cross = 1 + ConflictKind::Cross as u8;
                    self.kind[ij] = cross;
                    self.kind[ji] = cross;
                    self.s_on[ij] = si;
                    self.s_on[ji] = sj;
                }
            }
        }
    }

    fn write_rows(&mut self, net: &NetworkStore, node: i32, g0: usize, k: usize) -> NetworkStatus {
        let nd = net.nodes[node as usize];
        let minor_rules = nd.control == NodeControl::Yield || nd.control == NodeControl::Stop;
        for i in 0..k {
            let need = (0..k).filter(|&j| self.kind[i * MAX_LOCAL + j] != 0).count();
            let g = g0 + i;
            if self.conflicts_used as usize + need > self.conflicts.len() {
                return NetworkStatus::ConflictsFull;
            }
            let first = self.conflicts_used as usize;
            let mut count = 0usize;
            let mut mask = 0u64;
            for j in 0..k {
                let kind = self.kind[i * MAX_LOCAL + j];
                if kind == 0 {
                    continue;
                }
                let rel = self.compute_rel(net, &nd, g, g0 + j, minor_rules);
                let entry = ConflictEntry {
                    other_local: j as u8,
                    flags: (kind - 1) | ((rel as u8) << 2),
                    s_mine_cm: to_cm(self.s_on[i * MAX_LOCAL + j]),
                    s_other_cm: to_cm(self.s_on[j * MAX_LOCAL + i]),
                    reserved: 0,
                };
                mask |= 1u64 << j;
                // Вставка с сохранением порядка по s_mine (при равенстве - по other_local; j возрастает, поэтому после равных).
                let mut pos = first + count;
                while pos > first && self.conflicts[pos - 1].s_mine_cm > entry.s_mine_cm {
                    self.conflicts[pos] = self.conflicts[pos - 1];
                    pos -= 1;
                }
                self.conflicts[pos] = entry;
                count += 1;
            }
            self.first_conflict[g] = first as i32;
            self.conflict_count[g] = count as i32;
            self.conflict_mask[g] = mask;
            self.conflicts_used += count as i32;
        }
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

fn to_cm(s: f32) -> u16 {
    // `as i32` насыщает и NaN -> 0 (как DetMath.ToIntSat).
    let v = ((s * 100.0) + 0.5) as i32;
    v.clamp(0, i32::from(u16::MAX)) as u16
}

/// Минимальное расстояние^2 между полилиниями двух полос и s обеих в точке наибольшего сближения.
fn closest_approach(net: &NetworkStore, lane_a: i32, lane_b: i32) -> (f32, f32, f32) {
    let a = net.lanes[lane_a as usize];
    let b = net.lanes[lane_b as usize];
    let mut best = f32::MAX;
    let mut s_a = 0.0f32;
    let mut s_b = 0.0f32;
    for i in 0..(a.point_count - 1) {
        let p1 = net.lane_points[(a.first_point + i) as usize];
        let q1 = net.lane_points[(a.first_point + i + 1) as usize];
        let sa0 = net.lane_point_s[(a.first_point + i) as usize];
        let sa1 = net.lane_point_s[(a.first_point + i + 1) as usize];
        let ax0 = min_net(p1.x, q1.x) - CONFLICT_WIDTH;
        let ax1 = max_net(p1.x, q1.x) + CONFLICT_WIDTH;
        let az0 = min_net(p1.z, q1.z) - CONFLICT_WIDTH;
        let az1 = max_net(p1.z, q1.z) + CONFLICT_WIDTH;
        for j in 0..(b.point_count - 1) {
            let p2 = net.lane_points[(b.first_point + j) as usize];
            let q2 = net.lane_points[(b.first_point + j + 1) as usize];
            // Отрезки дальше CONFLICT_WIDTH друг от друга конфликта не дадут: отсекаем по AABB.
            if max_net(p2.x, q2.x) < ax0 || min_net(p2.x, q2.x) > ax1 || max_net(p2.z, q2.z) < az0 || min_net(p2.z, q2.z) > az1 {
                continue;
            }
            let (d2, s, t) = segment_dist_sq(p1, q1, p2, q2);
            if d2 < best {
                best = d2;
                s_a = sa0 + (s * (sa1 - sa0));
                let sb0 = net.lane_point_s[(b.first_point + j) as usize];
                s_b = sb0 + (t * (net.lane_point_s[(b.first_point + j + 1) as usize] - sb0));
            }
        }
    }
    (best, s_a, s_b)
}

/// Ближайшие точки двух отрезков (Ericson, Real-Time Collision Detection 5.1.9): (расстояние^2, s, t), s и t в [0, 1].
fn segment_dist_sq(p1: Vec2, q1: Vec2, p2: Vec2, q2: Vec2) -> (f32, f32, f32) {
    const EPS: f32 = 1e-9;
    let d1 = q1 - p1;
    let d2 = q2 - p2;
    let r = p1 - p2;
    let a = dot(d1, d1);
    let e = dot(d2, d2);
    let f = dot(d2, r);
    let s;
    let t;
    if a <= EPS && e <= EPS {
        s = 0.0;
        t = 0.0;
    } else if a <= EPS {
        s = 0.0;
        t = clamp01(f / e);
    } else {
        let c = dot(d1, r);
        if e <= EPS {
            t = 0.0;
            s = clamp01(-c / a);
        } else {
            let b = dot(d1, d2);
            let denom = (a * e) - (b * b);
            let mut s0 = if denom > EPS { clamp01(((b * f) - (c * e)) / denom) } else { 0.0 };
            let mut t0 = ((b * s0) + f) / e;
            if t0 < 0.0 {
                t0 = 0.0;
                s0 = clamp01(-c / a);
            } else if t0 > 1.0 {
                t0 = 1.0;
                s0 = clamp01((b - c) / a);
            }
            s = s0;
            t = t0;
        }
    }
    let c1 = p1 + (d1 * s);
    let c2 = p2 + (d2 * t);
    let diff = c1 - c2;
    (dot(diff, diff), s, t)
}

fn dot(a: Vec2, b: Vec2) -> f32 {
    (a.x * b.x) + (a.z * b.z)
}

/// Как в C# (`v < 0 ? 0 : v > 1 ? 1 : v`): NaN проходит как NaN (`f32::clamp` тоже).
fn clamp01(v: f32) -> f32 {
    v.clamp(0.0, 1.0)
}
