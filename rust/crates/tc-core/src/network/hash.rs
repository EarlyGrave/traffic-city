//! Сетевые хэши Rust-пилота (rust-pilot.md §5.2, «Уточнения R2»): порт `Metrics/NetworkHash.cs`. FNV-1a 64, порядок и состав - как в C#.

use crate::api::desc::{HASH_VERSION, PROFILES_TAG};
use crate::intersections::ConnectorStore;
use crate::util::Fnv1a64;

use super::store::NetworkStore;

/// `network_hash`: сеть после построения и `ConnectorStore` (разделы 1-7 спеки).
pub fn network_hash(net: &NetworkStore, cs: &ConnectorStore) -> u64 {
    let mut h = Fnv1a64::new();
    h.add_i32(HASH_VERSION);
    h.add_i32(net.node_count);
    h.add_i32(net.segment_count);
    h.add_i32(net.lane_count);
    h.add_i32(net.lane_point_count);
    h.add_i32(net.segment_point_count);
    h.add_i32(net.node_arm_count);
    h.add_i32(net.lane_next_count);
    h.add_i32(cs.count);
    h.add_i32(cs.conflicts_used);

    for n in &net.nodes[..net.node_count as usize] {
        h.add_f32(n.position.x);
        h.add_f32(n.position.z);
        h.add_f32(n.elevation);
        h.add_f32(n.radius);
        h.add_i32(n.control as i32);
        h.add_i32(n.first_arm);
        h.add_i32(n.arm_count);
        h.add_i32(n.first_connector);
        h.add_i32(n.connector_count);
        h.add_i32(n.signal);
        h.add_i32(n.zone);
        h.add_i32(n.generation);
    }

    for s in &net.segments[..net.segment_count as usize] {
        h.add_i32(s.node_a);
        h.add_i32(s.node_b);
        h.add_f32(s.length);
        h.add_f32(s.speed_limit);
        h.add_u8(s.kind as u8);
        h.add_u8(s.lanes_ab);
        h.add_u8(s.lanes_ba);
        h.add_u8(s.flags);
        h.add_i32(s.first_lane_ab);
        h.add_i32(s.first_lane_ba);
        h.add_i32(s.first_point);
        h.add_i32(s.point_count);
        h.add_i32(s.generation);
    }

    for (i, l) in net.lanes[..net.lane_count as usize].iter().enumerate() {
        h.add_u8(l.kind as u8);
        h.add_u8(l.lane_no);
        h.add_u8(l.direction);
        h.add_u8(l.flags);
        h.add_i32(l.segment);
        h.add_i32(l.node);
        h.add_i32(l.from_lane);
        h.add_i32(l.to_lane);
        h.add_f32(l.length);
        h.add_f32(l.speed_limit);
        h.add_i32(l.first_point);
        h.add_i32(l.point_count);
        h.add_i32(l.left);
        h.add_i32(l.right);
        h.add_i32(l.first_next);
        h.add_i32(l.next_count);
        h.add_i32(l.conflict_group);
        h.add_i32(l.generation);
        h.add_f32(net.lane_length[i]);
        h.add_i32(net.lane_gate_node[i]);
    }

    for i in 0..net.node_arm_count as usize {
        h.add_i32(net.node_arms[i]);
        h.add_u8(net.arm_flags[i]);
        h.add_f32(net.arm_dir[i].x);
        h.add_f32(net.arm_dir[i].z);
    }

    for &v in &net.lane_next[..net.lane_next_count as usize] {
        h.add_i32(v);
    }

    for p in &net.segment_points[..net.segment_point_count as usize] {
        h.add_f32(p.x);
        h.add_f32(p.z);
    }

    for i in 0..net.lane_point_count as usize {
        h.add_f32(net.lane_points[i].x);
        h.add_f32(net.lane_points[i].z);
        h.add_f32(net.lane_point_s[i]);
    }

    for i in 0..cs.count as usize {
        h.add_i32(cs.lane[i]);
        h.add_i32(cs.node[i]);
        h.add_i32(cs.in_lane[i]);
        h.add_i32(cs.out_lane[i]);
        h.add_f32(cs.length[i]);
        h.add_f32(cs.in_length[i]);
        h.add_u8(cs.local_bit[i]);
        h.add_u8(cs.maneuver[i]);
        h.add_u8(cs.from_arm[i]);
        h.add_u8(cs.flags[i]);
    }

    if let Some(ps) = &net.profiles {
        // Раздел профилей (ADR-0030, метка `FORP`; только режим профилей - legacy-хэш прежний): каталог, `segment.profile` и `lane.width` всех слотов.
        h.add_i32(PROFILES_TAG);
        ps.add_to_hash(&mut h);
        for s in &net.segments[..net.segment_count as usize] {
            h.add_i32(s.profile);
        }
        for l in &net.lanes[..net.lane_count as usize] {
            h.add_f32(l.width);
        }
    }

    h.value()
}

/// `headings_hash`: курс отрезков полос `lane_piece_heading` (atan2, системный libm; не блокирует golden).
pub fn headings_hash(net: &NetworkStore) -> u64 {
    let mut h = Fnv1a64::new();
    h.add_i32(HASH_VERSION);
    h.add_i32(net.lane_point_count);
    for &v in &net.lane_piece_heading[..net.lane_point_count as usize] {
        h.add_f32(v);
    }
    h.value()
}

/// `conflicts_hash`: по connector'ам `first_conflict, conflict_count, conflict_mask`, затем записи конфликтов.
pub fn conflicts_hash(cs: &ConnectorStore) -> u64 {
    let mut h = Fnv1a64::new();
    h.add_i32(HASH_VERSION);
    h.add_i32(cs.count);
    h.add_i32(cs.conflicts_used);
    for i in 0..cs.count as usize {
        h.add_i32(cs.first_conflict[i]);
        h.add_i32(cs.conflict_count[i]);
        h.add_u64(cs.conflict_mask[i]);
    }
    for e in &cs.conflicts[..cs.conflicts_used as usize] {
        h.add_u8(e.other_local);
        h.add_u8(e.flags);
        h.add_bytes(&e.s_mine_cm.to_le_bytes());
        h.add_bytes(&e.s_other_cm.to_le_bytes());
        h.add_bytes(&e.reserved.to_le_bytes());
    }
    h.value()
}
