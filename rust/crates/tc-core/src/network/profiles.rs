//! Каталог профилей дорог в SoA (road-profiles.md §3.1, ADR-0030). Порт `Network/RoadProfileStore.cs`.
//!
//! Создаётся один раз в `Sim::create` из проверенного `RoadProfilesDesc`, дальше только читается. Все ширины - целые мм;
//! метры (f32) получаются одной формулой `(offset_mm + width_mm * 0.5) * 0.001` - побитово как в C#.
//! Профиль задан в направлении рисования (A->B); сегмент, построенный против него, несёт `SEGMENT_MIRRORED`:
//! элементы читаются в обратном порядке, `forward` и `backward` меняются местами.

use crate::api::desc::RoadKind;
use crate::api::profiles::{RoadElemKind, RoadLaneDir, RoadProfilesDesc};
use crate::util::Fnv1a64;

pub struct RoadProfileStore {
    pub count: usize,
    pub module_mm: i32,

    // ---- по профилю ----
    pub first_elem: Vec<usize>,
    pub elem_count: Vec<usize>,
    pub width_mm: Vec<i32>,
    pub kind: Vec<RoadKind>,
    pub lanes_fwd: Vec<u8>,
    pub lanes_bwd: Vec<u8>,
    /// Индекс первой полосы профиля в `lane_offset_mm`: сначала forward (LaneNo 0 = правая), затем backward.
    pub first_lane: Vec<usize>,
    /// Ось -> внешний край крайней левой / правой полосы (внутренний край бордюра), мм.
    pub curb_l_mm: Vec<i32>,
    pub curb_r_mm: Vec<i32>,
    /// Ширина тротуара слева / справа, 0 - тротуара нет.
    pub walk_l_mm: Vec<i32>,
    pub walk_r_mm: Vec<i32>,
    /// Ось -> край профиля, мм (`width_mm / 2`).
    pub edge_mm: Vec<i32>,

    // ---- по элементу ----
    pub elem_kind: Vec<RoadElemKind>,
    pub elem_dir: Vec<u8>,
    pub elem_offset_mm: Vec<i32>,
    pub elem_width_mm: Vec<i32>,

    // ---- по полосе профиля ----
    pub lane_offset_mm: Vec<i32>,
    pub lane_width_mm: Vec<i32>,
}

impl RoadProfileStore {
    /// Каталог должен быть проверен `profiles::validate`; `None`-каталог даёт пустое хранилище.
    pub fn new(d: &RoadProfilesDesc) -> Self {
        let ps: &[_] = d.profiles.as_deref().unwrap_or(&[]);
        let n = ps.len();
        let elems: usize = ps.iter().map(|p| p.elements.len()).sum();
        let lanes: usize = ps
            .iter()
            .map(|p| p.elements.iter().filter(|e| e.kind == RoadElemKind::Lane).count())
            .sum();
        let mut s = Self {
            count: n,
            module_mm: d.module_mm,
            first_elem: vec![0; n],
            elem_count: vec![0; n],
            width_mm: vec![0; n],
            kind: vec![RoadKind::Street; n],
            lanes_fwd: vec![0; n],
            lanes_bwd: vec![0; n],
            first_lane: vec![0; n],
            curb_l_mm: vec![0; n],
            curb_r_mm: vec![0; n],
            walk_l_mm: vec![0; n],
            walk_r_mm: vec![0; n],
            edge_mm: vec![0; n],
            elem_kind: vec![RoadElemKind::Sidewalk; elems],
            elem_dir: vec![0; elems],
            elem_offset_mm: vec![0; elems],
            elem_width_mm: vec![0; elems],
            lane_offset_mm: vec![0; lanes],
            lane_width_mm: vec![0; lanes],
        };

        let (mut ei, mut li) = (0_usize, 0_usize);
        for (i, p) in ps.iter().enumerate() {
            let el = &p.elements;
            s.kind[i] = p.kind;
            s.width_mm[i] = p.width_mm;
            s.edge_mm[i] = p.width_mm / 2;
            s.first_elem[i] = ei;
            s.elem_count[i] = el.len();
            s.first_lane[i] = li;

            let fwd = el
                .iter()
                .filter(|e| e.kind == RoadElemKind::Lane && e.dir == RoadLaneDir::Forward)
                .count();
            let bwd = el
                .iter()
                .filter(|e| e.kind == RoadElemKind::Lane && e.dir == RoadLaneDir::Backward)
                .count();
            s.lanes_fwd[i] = fwd as u8;
            s.lanes_bwd[i] = bwd as u8;

            let mut x = -s.edge_mm[i];
            let mut left_edge = i32::MAX;
            let mut right_edge = i32::MIN;
            let (mut f_seen, mut b_seen) = (0_usize, 0_usize);
            for (e, el_e) in el.iter().enumerate() {
                s.elem_kind[ei + e] = el_e.kind;
                s.elem_dir[ei + e] = el_e.dir as u8;
                s.elem_offset_mm[ei + e] = x;
                s.elem_width_mm[ei + e] = el_e.width_mm;
                if el_e.kind == RoadElemKind::Lane {
                    // Слева направо: сначала backward (LaneNo растёт слева направо), затем forward (LaneNo растёт справа налево).
                    let slot = if el_e.dir == RoadLaneDir::Forward {
                        let sl = fwd - 1 - f_seen;
                        f_seen += 1;
                        sl
                    } else {
                        let sl = fwd + b_seen;
                        b_seen += 1;
                        sl
                    };
                    s.lane_offset_mm[li + slot] = x;
                    s.lane_width_mm[li + slot] = el_e.width_mm;
                    left_edge = left_edge.min(x);
                    right_edge = right_edge.max(x + el_e.width_mm);
                }
                x += el_e.width_mm;
            }

            s.curb_l_mm[i] = -left_edge;
            s.curb_r_mm[i] = right_edge;
            s.walk_l_mm[i] = match el.first() {
                Some(e) if e.kind == RoadElemKind::Sidewalk => e.width_mm,
                _ => 0,
            };
            s.walk_r_mm[i] = match el.last() {
                Some(e) if e.kind == RoadElemKind::Sidewalk => e.width_mm,
                _ => 0,
            };
            ei += el.len();
            li += fwd + bwd;
        }
        s
    }

    // ---- аксессоры (учитывают mirrored) ----

    /// Число полос A->B сегмента с профилем `p`.
    pub fn lanes_ab(&self, p: usize, mirrored: bool) -> i32 {
        i32::from(if mirrored { self.lanes_bwd[p] } else { self.lanes_fwd[p] })
    }

    /// Число полос B->A.
    pub fn lanes_ba(&self, p: usize, mirrored: bool) -> i32 {
        i32::from(if mirrored { self.lanes_fwd[p] } else { self.lanes_bwd[p] })
    }

    fn slot(&self, p: usize, mirrored: bool, dir: i32, lane_no: i32) -> usize {
        let d = if mirrored { 1 - dir } else { dir };
        let ln = lane_no as usize;
        self.first_lane[p] + if d == 0 { ln } else { usize::from(self.lanes_fwd[p]) + ln }
    }

    /// Смещение оси полосы от оси сегмента, м (+ вправо по A->B сегмента): `(offset_mm + width_mm * 0.5) * 0.001`.
    pub fn lane_center_m(&self, p: usize, mirrored: bool, dir: i32, lane_no: i32) -> f32 {
        let s = self.slot(p, mirrored, dir, lane_no);
        let c = (self.lane_offset_mm[s] as f32 + (self.lane_width_mm[s] as f32 * 0.5_f32)) * 0.001_f32;
        if mirrored { -c } else { c }
    }

    /// Ширина полосы, м.
    pub fn lane_width_m(&self, p: usize, mirrored: bool, dir: i32, lane_no: i32) -> f32 {
        self.lane_width_mm[self.slot(p, mirrored, dir, lane_no)] as f32 * 0.001_f32
    }

    /// Полуширина проезжей части `h` для отступа рукава (ADR-0028): `max(curb_l, curb_r)`, м; от `mirrored` не зависит.
    pub fn half_road_m(&self, p: usize) -> f32 {
        self.curb_l_mm[p].max(self.curb_r_mm[p]) as f32 * 0.001_f32
    }

    /// Каталог в хэш (только режим профилей): модуль, затем профили и их элементы - целые, порядок фиксирован.
    pub fn add_to_hash(&self, h: &mut Fnv1a64) {
        h.add_i32(self.count as i32);
        h.add_i32(self.module_mm);
        for i in 0..self.count {
            h.add_u8(self.kind[i] as u8);
            h.add_i32(self.width_mm[i]);
            h.add_i32(self.elem_count[i] as i32);
            for e in self.first_elem[i]..self.first_elem[i] + self.elem_count[i] {
                h.add_u8(self.elem_kind[e] as u8);
                h.add_u8(self.elem_dir[e]);
                h.add_i32(self.elem_width_mm[e]);
            }
        }
    }
}
